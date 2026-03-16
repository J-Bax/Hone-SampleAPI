# Replace full table scans and N+1 queries in order read endpoints

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:34-37`, `GetOrdersByCustomer` loads the **entire Orders table** into memory and filters client-side:

```csharp
var allOrders = await _context.Orders.ToListAsync();         // Full table scan
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At `OrdersController.cs:52-58`, `GetOrder` loads **all OrderItems** and then does N+1 product lookups:

```csharp
var allItems = await _context.OrderItems.ToListAsync();      // Full table scan
var items = allItems.Where(i => i.OrderId == id).ToList();   // Client-side filter

foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);  // N+1
```

The Orders table starts with 100 seed rows but **grows continuously** — every k6 VU iteration creates 2 orders (POST /api/orders + POST /Checkout). At 1006 RPS over a 2-minute test, thousands of orders accumulate. The OrderItems table grows even faster (2-3 items per order).

## Theory

`GetOrdersByCustomer` performs a full table scan followed by case-insensitive client-side filtering — the exact anti-pattern fixed in other controllers (experiments 1-5). As the Orders table grows across experiments, this query's cost scales linearly. `GetOrder` compounds the problem: a full OrderItems table scan plus N+1 per-item product lookups means O(items_in_DB) + O(items_in_order) queries per call. While these endpoints are not in the current k6 scenario, they represent the most severe unoptimized code paths remaining — identical patterns to the issues that yielded the largest improvements in experiments 1-5.

## Proposed Fixes

1. **GetOrdersByCustomer (line 34-37):** Replace with server-side filter:
   ```csharp
   var filtered = await _context.Orders.AsNoTracking()
       .Where(o => o.CustomerName == customerName)
       .ToListAsync();
   ```

2. **GetOrder (lines 52-68):** Replace full OrderItems scan with server-side filter, and batch product lookups:
   ```csharp
   var items = await _context.OrderItems.AsNoTracking()
       .Where(i => i.OrderId == id).ToListAsync();
   var productIds = items.Select(i => i.ProductId).Distinct().ToList();
   var products = await _context.Products.AsNoTracking()
       .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
   ```

## Expected Impact

- These endpoints are **not in the current k6 scenario**, so measured p95/RPS impact is ~0%
- However, the Orders/OrderItems tables grow every test run — these endpoints will degrade severely if exercised
- Fixes identical anti-patterns to experiments 1-5 which yielded the largest gains (7546ms → 793ms)
- Prevents future regression if k6 scenarios are expanded to include order lookup flows
