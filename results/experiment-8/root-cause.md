# Root Cause Analysis — Experiment 8

> Generated: 2026-03-15 07:08:05 | Classification: narrow — The full table scan (ToListAsync + client-side Where) and N+1 product lookups in LoadCart/OnPostClearAsync, plus per-item SaveChangesAsync in the clear loop, can all be fixed within this single PageModel file by adding server-side .Where() filtering, .Include(c => c.Product) to eliminate N+1, and batching SaveChangesAsync outside the loop — no dependency, schema, or API contract changes required.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 542.973235ms | 1596.242785ms |
| Requests/sec | 1164.1 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# Full CartItems table scan with N+1 lookups and per-item SaveChanges

> **File:** `SampleApi/Pages/Cart/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Cart/Index.cshtml.cs:109-110`, `LoadCart()` loads the entire CartItems table:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

Then at lines 115-130, it performs N+1 product lookups per session item:

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
    ...
}
```

Additionally, `OnPostClearAsync` at lines 91-98 loads ALL cart items, filters in memory, then does per-item `SaveChangesAsync()` inside a loop:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

## Theory

The CartItems table grows continuously during the k6 load test — every VU iteration adds items via both the API and Razor page flows. Under 500 VUs, the table can contain thousands of rows at any point. Loading the ENTIRE table for every Cart page view (just to filter to one session's ~1 item) is extremely wasteful and gets progressively worse as the test runs. The N+1 product lookups add a DB round-trip per cart item. The per-item `SaveChangesAsync` in `OnPostClearAsync` generates a separate SQL transaction per deletion.

The CPU profiler's TDS parsing hotspots (8.5% in `TryReadChar`, 5.2% in `TryReadColumnInternal`) and the memory profiler's 1,273 MB/sec allocation rate both reflect the massive data volume being pulled through this path. The `LoadCart()` method is called from `OnGetAsync`, `OnPostUpdateQuantityAsync`, `OnPostRemoveAsync`, and `OnPostClearAsync` — so every Cart page interaction triggers the full table scan.

## Proposed Fixes

1. **Server-side session filtering + batch product lookup:** Replace `CartItems.ToListAsync()` with `_context.CartItems.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync()`. Replace the N+1 product loop with a batch lookup: collect product IDs, then `_context.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`.

2. **Batch clear with single SaveChanges:** In `OnPostClearAsync`, use `_context.CartItems.Where(c => c.SessionId == sessionId)` with `RemoveRange` and a single `SaveChangesAsync()` call instead of per-item saves.

## Expected Impact

- p95 latency: Per-request latency should drop ~40ms by eliminating the full table scan on a growing table and N+1 queries.
- Throughput: Single SaveChanges in clear operation reduces DB round-trips from N to 1.
- Overall p95 improvement: ~3.8% (5.6% traffic share × 40ms / 583ms). Impact increases as the test progresses due to the growing table.

