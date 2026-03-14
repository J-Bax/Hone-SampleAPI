# CreateOrder has N+1 product lookups and double SaveChanges

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:98-99`, the order is saved immediately just to obtain an auto-generated ID:

```csharp
_context.Orders.Add(order);
await _context.SaveChangesAsync(); // Save to get order ID
```

At `OrdersController.cs:103-118`, each order item triggers a separate `FindAsync` to look up the product price:

```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
    ...
}
```

Then at line 122, a second `SaveChangesAsync()` persists the order items and updated total. With the k6 scenario sending 2 items per order, this is 4 database round trips per request (1 save + 2 FindAsync + 1 save).

## Theory

Every VU iteration creates one order (7.7% of traffic). The N+1 pattern means each order creation requires N+2 database round trips (2 saves + N product lookups). Under 500 VUs, this creates significant connection pool contention and serialized I/O waits. Each round trip adds ~2-5ms of network/protocol overhead on LocalDB, so 4 round trips waste ~8-15ms compared to a batched approach.

## Proposed Fixes

1. **Batch product lookup:** Replace the per-item `FindAsync` loop with a single query: `var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList(); var products = await _context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);` Then look up prices from the dictionary in the loop.

2. **Single SaveChangesAsync:** EF Core tracks the Order entity and will assign the ID after a single `SaveChangesAsync`. Add both the Order and all OrderItems to the context, then call `SaveChangesAsync` once. Use a temporary variable for the order reference since EF will populate `order.Id` after save.

## Expected Impact

- p95 latency reduction per request: ~20-35ms (eliminating 2-3 unnecessary round trips)
- Overall p95 improvement: ~2-3% (7.7% traffic share × ~25ms saving)
- Additional benefit: reduced connection pool pressure under high concurrency
