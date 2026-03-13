# Fix N+1 queries and client-side cart filtering

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-26`, `GetCart` loads **all** cart items into memory then filters by session:

```csharp
var allItems = await _context.CartItems.ToListAsync();              // line 25
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList(); // line 26
```

Then at lines 33, it executes a separate `FindAsync` for each cart item's product (classic N+1):

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId); // line 33
```

At `CartController.cs:69-71`, `AddToCart` loads all cart items to check for an existing item:

```csharp
var allItems = await _context.CartItems.ToListAsync();              // line 69
var existing = allItems.FirstOrDefault(c =>                         // line 70
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

At `CartController.cs:140-147`, `ClearCart` loads all cart items, filters, then calls `SaveChangesAsync()` inside a loop:

```csharp
var allItems = await _context.CartItems.ToListAsync();              // line 140
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList(); // line 141
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();                              // line 146 — per-item save!
}
```

The k6 baseline hits `POST /api/cart`, `GET /api/cart/{sessionId}`, and `DELETE /api/cart/session/{sessionId}` every iteration (baseline.js lines 77, 85, 90). The CartItems table **grows continuously** during the load test as 500 VUs add items.

## Theory

Cart endpoints have three compounding problems: (1) loading the entire CartItems table on every request — and this table grows throughout the test as VUs add items, meaning performance degrades over time; (2) N+1 queries in GetCart where each cart item triggers a separate `FindAsync` for its product; (3) per-item `SaveChangesAsync` in ClearCart creating N separate SQL round-trips.

With 500 VUs, each adding an item then reading/clearing their cart, the CartItems table could grow to thousands of rows mid-test. Each GetCart call loads all those rows, filters to ~1 item, then does a FindAsync. Each ClearCart loads all rows, filters, then does individual DELETE+SaveChanges calls. The growing table size creates an escalating performance degradation pattern.

The SortedDictionary/SortedSet CPU hotspot (3% of CPU) from the profiling report may be related to EF Core's internal tracking of the growing CartItems entity set.

## Proposed Fixes

1. **Server-side filtering:** Replace `ToListAsync()` + LINQ `.Where()` with server-side `_context.CartItems.Where(c => c.SessionId == sessionId)` in GetCart (line 25-26), AddToCart (line 69-71), and ClearCart (line 140-141). Use `.AsNoTracking()` for read-only GetCart.

2. **Eliminate N+1 in GetCart:** Replace the `FindAsync` loop (lines 31-47) with a single join query: `_context.CartItems.Where(c => c.SessionId == sessionId).Join(_context.Products, ci => ci.ProductId, p => p.Id, (ci, p) => new { ... }).ToListAsync()`. Alternatively, use `.FirstOrDefaultAsync()` with a predicate in AddToCart (line 69-71) instead of loading all items.

3. **Batch delete in ClearCart:** Replace the per-item `SaveChangesAsync` loop (lines 143-147) with `RemoveRange()` + a single `SaveChangesAsync()` call, or use `ExecuteDeleteAsync()` for a single SQL DELETE statement.

## Expected Impact

- **p95 latency:** ~10-15% reduction. The N+1 elimination in GetCart alone removes N+1 SQL round-trips per request. The escalating degradation from growing CartItems table is eliminated.
- **RPS:** ~10-15% increase from fewer SQL round-trips and less data transfer.
- **GC pressure:** Moderate reduction — cart items are a smaller table than Products or Reviews, but the growing-table pattern means late-test allocations are disproportionately large.
- **Error rate:** Should remain at 0%.
