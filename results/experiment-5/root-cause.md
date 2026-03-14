# Root Cause Analysis — Experiment 5

> Generated: 2026-03-14 13:27:00 | Classification: narrow — The optimization (adding .Include(ci => ci.Product) to CartItems query and removing the loop-based FindAsync calls) is contained entirely to the LoadCart() method body, requires no package changes, no database migrations, no API contract changes, and no test modifications.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 481.487745ms | 2054.749925ms |
| Requests/sec | 1323.9 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Cart page loads entire CartItems table and has N+1 product lookups

> **File:** `SampleApi/Pages/Cart/Index.cshtml.cs` | **Scope:** narrow

## Evidence

The Cart page's `LoadCart` method (called by both `OnGetAsync` and all POST handlers) loads the entire CartItems table at `Index.cshtml.cs:109`:
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```
Then performs N+1 product lookups at `Index.cshtml.cs:117`:
```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

Additionally, `OnPostClearAsync` at lines 91-98 has the same full-table scan plus per-item SaveChangesAsync:
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync(); // per-item round-trip
}
```

## Theory

The CartItems table grows with concurrent VU sessions. Under 500 VUs, each VU adds cart items (via `POST /Products/Detail/{id}`), so the table accumulates hundreds to thousands of rows. Loading the full table for every cart view wastes memory and CPU on materializing and filtering unrelated sessions' items. The N+1 product lookups add one DB round-trip per cart item. The `OnPostClearAsync` handler compounds this with per-item deletes.

The Cart page is ~5.6% of traffic (1 of 18 requests), but `LoadCart` is also called after every POST handler on this page (update quantity, remove, clear), amplifying its impact.

## Proposed Fixes

1. **Server-side filtering:** Replace `_context.CartItems.ToListAsync()` with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()` at line 109 (and line 91). Replace N+1 product lookups with a single batch query using `Where(p => productIds.Contains(p.Id))`.

2. **Batch cart clearing:** In `OnPostClearAsync`, use `RemoveRange` and a single `SaveChangesAsync` instead of per-item saves.

## Expected Impact

- **p95 latency:** ~3-5% overall p95 reduction (~50-80ms). Server-side filtering eliminates the growing full-table scan, and batch product lookup removes N+1 queries.
- **Memory:** Reduced allocation per request as only the session's items are materialized instead of the full table.
- **GC:** Marginal improvement in allocation rate, contributing to lower GC pause frequency.

