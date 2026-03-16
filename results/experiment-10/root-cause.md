# Root Cause Analysis — Experiment 10

> Generated: 2026-03-15 17:01:20 | Classification: narrow — Batching product lookups (replacing the N+1 FindAsync loop with a single query using a collected list of ProductIds) and adding AsNoTracking are purely internal implementation changes within the single CreateOrder method body, requiring no new dependencies, no schema changes, and no API contract modifications.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 591.942145ms | 7546.103045ms |
| Requests/sec | 980.9 | 125.5 |
| Error Rate | 0% | 0% |

---
# Batch product lookups and add AsNoTracking in CreateOrder

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:103–107`, the `CreateOrder` method performs a sequential `FindAsync` for each order item to look up the product:

```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
    if (product == null)
        continue;
```

The k6 scenario sends 2 items per order (lines with `seededId(100, 3)` and `seededId(100, 4)`), resulting in 2 sequential round trips per request. Products are loaded with change tracking enabled despite being read-only (only `product.Price` is used at line 117).

Additionally, `GetOrdersByCustomer` at line 35 loads **all orders** into memory and filters client-side:

```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

And `GetOrder` at line 52 loads **all order items** into memory:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();
```

Followed by N+1 product lookups at lines 58–59 for each item.

## Theory

The `CreateOrder` N+1 pattern is the most impactful issue since `POST /api/orders` is called every k6 iteration (~5.6% of traffic). Each sequential `FindAsync` is a separate database round trip. Under 500 concurrent VUs, these extra round trips:

1. Hold a DB connection for the duration of all sequential lookups (~2 round trips)
2. Contribute to connection pool pressure that affects all other requests
3. Load products with change tracking, adding unnecessary entries to EF's identity map and increasing allocation volume (GC analysis shows 392 MB/sec allocation rate)

The `GetOrdersByCustomer` and `GetOrder` methods have worse patterns (full table scans + N+1) but are not in the k6 scenario — they represent latent performance debt that would surface if traffic patterns change.

## Proposed Fixes

1. **Batch product lookup in `CreateOrder`**: Replace the per-item `FindAsync` loop with a single batch query using `AsNoTracking`:
   ```csharp
   var productIds = request.Items.Select(i => i.ProductId).ToList();
   var products = await _context.Products
       .AsNoTracking()
       .Where(p => productIds.Contains(p.Id))
       .ToDictionaryAsync(p => p.Id);
   ```
   Then iterate and use `products.TryGetValue(itemReq.ProductId, out var product)` instead of `FindAsync`.

2. **Fix `GetOrdersByCustomer`** (line 35): Replace `ToListAsync()` + client-side `Where` with a server-side `Where` clause and add `AsNoTracking()`.

3. **Fix `GetOrder`** (line 52): Replace `_context.OrderItems.ToListAsync()` with `.Where(i => i.OrderId == id).ToListAsync()` and batch product lookups as in CreateOrder.

## Expected Impact

- **p95 latency for POST /api/orders**: ~15ms reduction from eliminating 1 DB round trip and change tracking overhead.
- **Overall p95**: Modest direct improvement (~0.14%) given 5.6% traffic share, but the connection pool pressure reduction has cascading benefits for other endpoints.
- **Allocation**: Reduced per-request allocations from removing change tracking on read-only product entities.
- Fixing `GetOrdersByCustomer` and `GetOrder` addresses latent performance debt for future traffic pattern changes.

