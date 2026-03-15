# Triple full table scan with N+1 on growing tables in order history

> **File:** `SampleApi/Pages/Orders/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Orders/Index.cshtml.cs:40-43`, the order history page loads ALL orders and filters in memory:

```csharp
var allOrders = await _context.Orders.ToListAsync();
Orders = allOrders
    .Where(o => o.CustomerName.Equals(customer, StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(o => o.OrderDate)
    .ToList();
```

At line 46, it loads ALL order items:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
```

At lines 53-55, it performs N+1 Product lookups for each order item:

```csharp
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
    // ...
}
```

## Theory

The Orders and OrderItems tables grow continuously during the k6 test. Every iteration creates 2 orders (1 via API, 1 via Checkout POST) with 2-3 items each. At 500 VUs doing back-to-back iterations, thousands of orders accumulate within seconds. The full table scan at line 40 materializes ALL of them — including orders from every other VU — with change tracking enabled.

The cascading pattern is devastating: full scan Orders (growing) → full scan OrderItems (growing faster, ~2-3x orders) → N+1 Product lookups per item. Under stress load, a single request to this page could easily materialize thousands of entities across 3 separate queries plus dozens of individual FindAsync calls.

This progressive degradation explains why the p95 only improved 4.4% from baseline despite 3 prior optimizations — the growing-table endpoints get worse as the test runs, pulling up tail latency in the stress phase.

## Proposed Fixes

1. **Server-side customer filtering:** Replace line 40's full scan with `_context.Orders.Where(o => o.CustomerName == customer).OrderByDescending(o => o.OrderDate).ToListAsync()`. Note: use case-insensitive collation or `EF.Functions.Like()` if case-insensitive matching is needed.

2. **Server-side OrderItems filter:** Replace line 46's full scan with `_context.OrderItems.Where(oi => orderIds.Contains(oi.OrderId)).ToListAsync()` where `orderIds` is the list of matching order IDs.

3. **Batch Product lookup:** Replace the N+1 loop at lines 53-55 with a single `_context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)` call using the distinct ProductIds from the filtered order items.

## Expected Impact

- p95 latency: ~250ms reduction on affected requests (eliminates scans of growing tables + N+1)
- The improvement compounds over the test duration — the heaviest stress-phase requests see the biggest benefit
- Overall p95 improvement: ~0.9% (5.6% of traffic * 250ms / 1527ms p95)
- GC: noticeable reduction in Gen2 pressure from fewer large materializations
