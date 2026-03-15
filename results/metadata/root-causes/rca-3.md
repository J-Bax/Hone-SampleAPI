# Full table scans and N+1 queries across all order read/write endpoints

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:35-37`, GetOrdersByCustomer loads the **entire Orders table** into memory and filters client-side:

```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At `OrdersController.cs:52-53`, GetOrder loads the **entire OrderItems table**:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();
```

At `OrdersController.cs:56-58`, GetOrder has an **N+1 product lookup loop**:

```csharp
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

At `OrdersController.cs:103-105`, CreateOrder uses **per-item sequential product lookups** instead of batching:

```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
```

The Orders table grows to ~28,000 rows and OrderItems to ~56,000+ rows during the load test (each VU creates an order every iteration). None of the read queries use `AsNoTracking()`, adding change tracker overhead to already expensive full table scans.

## Theory

CreateOrder (POST /api/orders) is called every k6 iteration (~5.6% of traffic) and performs 4 sequential DB round trips: (1) SaveChanges for order ID, (2) FindAsync product 1, (3) FindAsync product 2, (4) SaveChanges for items. Batching the product lookups into one query saves one round trip per request. Under 500 VUs, this reduces connection pool contention.

GetOrdersByCustomer and GetOrder are not exercised by the k6 scenario but represent severe performance hazards on the rapidly growing Orders/OrderItems tables. Loading 28K+ orders or 56K+ order items into memory for client-side filtering would cause catastrophic latency and memory pressure under any real-world read traffic. The N+1 product lookup in GetOrder compounds this with additional per-item DB round trips.

The lack of `AsNoTracking()` on all read operations means every materialized entity enters the change tracker, contributing to the overall allocation pressure and GC overhead.

## Proposed Fixes

1. **Batch product lookups in CreateOrder (lines 103–118):** Collect all `productId` values from `request.Items`, fetch them in one query with `_context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`, then iterate over request items using the dictionary lookup.

2. **Fix GetOrdersByCustomer (lines 35–37):** Replace full table scan + in-memory filter with `_context.Orders.AsNoTracking().Where(o => o.CustomerName == customerName).ToListAsync()`.

3. **Fix GetOrder (lines 52–68):** Replace OrderItems full table scan with `.AsNoTracking().Where(i => i.OrderId == id)` and replace N+1 product lookups with a batch query using `Where(p => productIds.Contains(p.Id)).ToDictionaryAsync()`.

## Expected Impact

- **p95 latency:** Estimated ~5–15ms improvement for CreateOrder requests from saving one DB round trip and reduced connection pool wait time.
- **RPS:** Marginal improvement (~0.5–1%) from reduced DB round trips on the CreateOrder path.
- **Defensive value:** The read endpoint fixes prevent catastrophic degradation on the growing Orders (28K+) and OrderItems (56K+) tables if any real-world traffic pattern exercises them.
