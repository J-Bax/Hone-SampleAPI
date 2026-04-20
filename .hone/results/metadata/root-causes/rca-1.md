# Cart endpoints load entire CartItems table and N+1 query products

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-26`, `GetCart` loads every cart item in the database into memory then filters by session:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

Then at lines 31-33, each session item triggers a separate product lookup (N+1):

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

The same pattern recurs in `AddToCart` at lines 69-71:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

And in `ClearCart` at lines 140-146, all items are loaded and then removed one-by-one with a `SaveChangesAsync` per iteration:

```csharp
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

## Theory

The cart endpoints are hit ~8 times per VU iteration (2× POST add, 1× GET cart, 1× PUT update, 1× DELETE remove, plus cart page, checkout GET/POST, and post-checkout cart check), representing roughly 40% of all traffic. Every call to `GetCart` or `AddToCart` pulls the entire `CartItems` table into memory instead of pushing the `WHERE SessionId = @p` filter to the database. As the number of concurrent sessions grows under load, this transfers increasingly large result sets. The N+1 product lookups in `GetCart` add sequential database round trips proportional to items in the cart. The `ClearCart` method's per-item `SaveChangesAsync` causes unnecessary write round trips.

## Proposed Fixes

1. **Server-side filtering:** Replace `_context.CartItems.ToListAsync()` + LINQ `.Where()` with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()` at lines 25-26, 69-71, and 140-141.
2. **Eager-load products:** For `GetCart`, use `.Include(c => c.Product)` or a join query to eliminate the N+1 loop at lines 31-33 (requires a navigation property or a manual join).
3. **Batch SaveChanges in ClearCart:** Move `SaveChangesAsync()` outside the loop at line 146, calling it once after all removes.

## Expected Impact

- p95 latency: reduction of ~3-5ms on cart-related requests by eliminating full table scans and N+1 queries.
- Overall p95 improvement: ~8-12% given cart endpoints represent ~40% of traffic.
- The largest gain comes from not loading the entire CartItems table on every request.