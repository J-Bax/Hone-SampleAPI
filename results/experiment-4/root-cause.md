# Root Cause Analysis — Experiment 4

> Generated: 2026-03-15 05:15:01 | Classification: narrow — All issues — per-item SaveChangesAsync loops (lines 84-100, 106-110), full-table CartItems scans filtered in-memory (lines 61-62, 124-125), and N+1 product lookups (lines 86, 132) — are implementation internals within a single PageModel file and can be fixed by batching saves, adding server-side Where filters, and using Include/join queries without changing any API contract, dependency, or additional file.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 1526.65415ms | 1596.242785ms |
| Requests/sec | 494.4 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# Per-item SaveChanges and full table scans in Checkout

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Checkout/Index.cshtml.cs:61-62`, the POST handler loads ALL cart items from the database and filters in memory:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();
```

At lines 84-99, each cart item triggers a separate Product lookup AND a separate `SaveChangesAsync()`:

```csharp
foreach (var cartItem in sessionItems)
{
    var product = await _context.Products.FindAsync(cartItem.ProductId);
    // ...
    _context.OrderItems.Add(new OrderItem { /* ... */ });
    await _context.SaveChangesAsync(); // line 99 — per-item save!
}
```

At lines 106-109, a SECOND per-item save loop removes cart items one at a time:

```csharp
foreach (var cartItem in sessionItems)
{
    _context.CartItems.Remove(cartItem);
    await _context.SaveChangesAsync(); // line 109 — another per-item save!
}
```

`LoadCartSummary()` at lines 124-125 repeats the full table scan, and lines 130-132 do another N+1 Product lookup loop.

## Theory

The POST handler issues `3N + 3` database round trips per request (where N = cart item count): 1 full CartItems scan + N FindAsync calls + 1 SaveChanges for the Order + N SaveChanges for OrderItems + 1 SaveChanges for total update + N SaveChanges for cart removal. Under 500 VUs, each round trip competes for the connection pool, causing cascading waits. The full CartItems scan worsens as VUs create carts concurrently — the table grows throughout the test, materializing all rows including those from other sessions.

The `LoadCartSummary()` method called by GET has the same full-scan + N+1 pattern, meaning both GET and POST /Checkout are expensive.

Every materialized entity goes through EF Core change tracking (confirmed by the 5.6% CPU in NavigationFixer.InitialFixup), and the per-item saves generate enormous allocation pressure contributing to the 206 GB total / 222 Gen2 collections observed in GC profiling.

## Proposed Fixes

1. **Replace full table scan with server-side filter:** Change `_context.CartItems.ToListAsync()` + in-memory `.Where()` to `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()` in both `OnPostAsync()` (line 61) and `LoadCartSummary()` (line 124).

2. **Batch all saves into a single SaveChangesAsync:** In `OnPostAsync()`, accumulate all OrderItem additions and cart removals, then call `SaveChangesAsync()` once at the end instead of per-item. Remove the per-item save at line 99 and the per-item save loop at lines 106-109, replacing with a single `_context.CartItems.RemoveRange(sessionItems); await _context.SaveChangesAsync();`.

3. **Batch Product lookups:** Replace the N+1 `FindAsync` loop with a single query: `_context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)` in both `OnPostAsync()` and `LoadCartSummary()`.

## Expected Impact

- p95 latency: ~350ms reduction on affected requests (from ~3N+3 round trips down to ~3 total)
- RPS: moderate improvement from reduced connection pool contention
- Overall p95 improvement: ~2.5% (11.1% of traffic * 350ms / 1527ms p95)
- GC pressure: significant reduction from fewer entity materializations and fewer SaveChanges allocations

