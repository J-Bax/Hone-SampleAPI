# Root Cause Analysis — Experiment 19

> Generated: 2026-03-10 06:55:18 | Classification: narrow — Full table scans and N+1 queries can be fixed entirely within this file by adding .Where() filters and .Include() eager loading to existing DbContext queries, without modifying dependencies, database schema, or other files.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 403.18154ms | 888.549155000001ms |
| Requests/sec | 1365.2 | 683.2 |
| Error Rate | 0% | 0% |

---
# Full table scans and N+1 queries in Cart page

> **File:** `SampleApi/Pages/Cart/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Cart/Index.cshtml.cs:109`, the `LoadCart()` method loads the **entire** CartItems table into memory:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

This full table scan is called from **four** handlers: `OnGetAsync` (line 52), `OnPostUpdateQuantityAsync` (line 69), `OnPostRemoveAsync` (line 84), and `OnPostClearAsync` (line 100) — meaning every cart page interaction triggers it.

At `Pages/Cart/Index.cshtml.cs:117`, an N+1 query pattern fetches each product individually inside a loop:

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

Additionally, at `Pages/Cart/Index.cshtml.cs:91-98`, `OnPostClearAsync` repeats the full table scan and calls `SaveChangesAsync()` per item:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

The CPU hotspot report shows SQL Server engine internals consuming ~16% and TDS parsing ~5.5% of CPU — directly caused by these over-fetching patterns.

## Theory

The CartItems table grows with every user session. Loading the full table to filter one session means the database must scan, serialize, and transmit all rows over TDS, then the app deserializes and discards most of them. Under load with many concurrent sessions, this creates massive unnecessary I/O.

The N+1 product lookup in `LoadCart()` issues a separate SQL round-trip per cart item. For a cart with N items, this means N+1 queries (1 for cart items + N for products). Each round-trip includes connection overhead, TDS framing, and async state machine allocations — explaining the high Gen0→Gen1 promotion rate (84%) in the memory profile.

The per-item `SaveChangesAsync()` in `OnPostClearAsync` generates N separate DELETE transactions instead of one batch, multiplying round-trips.

## Proposed Fixes

1. **Server-side filtering with join:** Replace the full table scan + loop with a single server-side query filtered by `SessionId`. Use a batch product lookup via `Where(p => productIds.Contains(p.Id))` instead of per-item `FindAsync`. Add `AsNoTracking()` for read-only paths.

2. **Batch SaveChanges in OnPostClearAsync:** Replace the per-item remove+save loop with `RemoveRange()` on the filtered items and a single `SaveChangesAsync()` call. Use a server-side `Where(c => c.SessionId == sessionId)` query instead of the full table scan.

## Expected Impact

- p95 latency: ~10-20ms reduction (cart page loads drop from O(N_total + N_session) queries to 2 queries)
- RPS: ~3-5% improvement from reduced database round-trips and connection contention
- Memory: Reduced allocation rate from eliminating full-table materialization and per-item async overhead
- The SessionId index already exists (line 65-66 in AppDbContext.cs), so server-side filtering will be an efficient index seek

