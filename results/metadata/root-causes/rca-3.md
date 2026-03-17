# Combine two-query GetCart into single join query

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-35`, the GetCart endpoint executes two sequential DB round trips:

```csharp
// Round trip 1: get cart items
var sessionItems = await _context.CartItems
    .AsNoTracking()
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();

var productIds = sessionItems.Select(c => c.ProductId).ToList();
// Round trip 2: get product details
var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .Select(p => new { p.Id, p.Name, p.Price })
    .ToDictionaryAsync(p => p.Id);
```

The same two-query pattern appears in the Cart page (`Pages/Cart/Index.cshtml.cs:107-122`) and Checkout page (`Pages/Checkout/Index.cshtml.cs:125-138`), but this fix targets the API endpoint specifically.

The CPU profiler shows EF Core's `SingleQueryingEnumerable.MoveNextAsync` at 5.85% inclusive CPU, and the async state machine overhead (Start + ExecutionContext changes) at ~5.3% — both inflated by the number of separate async DB operations per request. Each round trip incurs network latency to LocalDB, connection pool checkout, command execution, and result materialization.

## Theory

Two sequential DB round trips double the network latency and connection pool hold time compared to a single query. Under 500 VU stress load, this means each GetCart request holds a connection for both queries sequentially, increasing connection pool contention (the profiler detected SemaphoreSlim.Wait — 355 samples — suggesting connection pool pressure). A single JOIN query fetches cart items with their product details in one round trip, halving the per-request connection hold time and eliminating one full async state machine cycle.

Additionally, the intermediate `List<CartItem>` and `Dictionary<int, ...>` allocations between the two queries contribute to the Gen0→Gen1 promotion pattern identified by the GC profiler (80% Gen1/Gen0 survival ratio, ~287 MB/sec allocation rate).

## Proposed Fixes

1. **Replace two queries with a single LINQ join:** At `CartController.cs:25-56`, replace the two-query pattern with a single join query that fetches cart items and product details together:
   ```csharp
   var cartData = await _context.CartItems
       .AsNoTracking()
       .Where(c => c.SessionId == sessionId)
       .Join(_context.Products,
           c => c.ProductId, p => p.Id,
           (c, p) => new { c.Id, c.ProductId, ProductName = p.Name,
               ProductPrice = p.Price, c.Quantity, c.AddedAt })
       .ToListAsync();
   ```
   Then build the response directly from `cartData` without intermediate collections. This eliminates one DB round trip, one intermediate List allocation, and the Dictionary allocation.

## Expected Impact

- **p95 latency:** Estimated 3–7ms reduction per GetCart request from eliminating one DB round trip
- **RPS:** Marginal improvement from reduced connection pool hold time and fewer allocations
- **Overall p95 improvement:** ~0.4–0.8% — at 5.56% traffic share, the per-request savings compound with reduced GC pressure
