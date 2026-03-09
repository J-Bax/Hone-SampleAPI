# Order queries load all rows then filter client-side with N+1

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:35-37`, `GetOrdersByCustomer` loads every order into memory:

```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At lines 52-53, `GetOrder` loads the entire `OrderItems` table to find items for one order:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();
```

Then at lines 56-58, each matching order item triggers another query:

```csharp
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

## Theory

As the load test creates orders via `POST /api/orders`, the Orders and OrderItems tables grow continuously. `GetOrdersByCustomer` fetches every order ever created to find one customer's orders — O(n) in total orders instead of O(k) in matching orders. The case-insensitive comparison in C# also means SQL Server can't use any index optimization.

`GetOrder` has the same pattern with OrderItems plus an N+1 for product details. For an order with 3 items, that's: 1 FindAsync(order) + 1 full table scan(OrderItems) + 3 FindAsync(products) = 5 queries instead of 1-2.

## Proposed Fixes

1. **Server-side filtering:** Replace `ToListAsync()` + LINQ-to-Objects with `Where()` clauses before materialization. For `GetOrdersByCustomer`, use `_context.Orders.Where(o => o.CustomerName == customerName)` (SQL Server default collation is case-insensitive). For `GetOrder`, use `_context.OrderItems.Where(i => i.OrderId == id)` and join with Products to eliminate the N+1 loop.

2. **Add index on OrderItem.OrderId and Order.CustomerName:** Configure indexes in `OnModelCreating` to support the filtered queries.

## Expected Impact

- p95 latency: ~10-15% reduction. Order detail and customer lookup endpoints will go from full table scans to indexed lookups.
- RPS: ~8-12% increase. Fewer queries per request and less data transferred reduces SQL Server and connection pool contention.
- The impact is slightly lower than Cart because order reads may be less frequent than cart operations in typical e-commerce load test scenarios.
