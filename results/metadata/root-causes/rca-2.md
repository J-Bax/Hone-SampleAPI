# Two full table scans and N+1 in Orders page

> **File:** `SampleApi/Pages/Orders/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Orders/Index.cshtml.cs:40`, the Orders page loads **all** orders into memory:

```csharp
var allOrders = await _context.Orders.ToListAsync();
Orders = allOrders
    .Where(o => o.CustomerName.Equals(customer, StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(o => o.OrderDate)
    .ToList();
```

At line 46, it then loads **all** order items into memory:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
```

At lines 54-55, it performs N+1 product lookups per order item:

```csharp
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

This method issues 2 full table scans + N individual product queries per page load. Neither query uses `AsNoTracking()`.

The CPU profile shows `SingleQueryingEnumerable.MoveNextAsync` at 25% inclusive and SQL Server internals at 16% — these full table scans are a significant contributor.

## Theory

The Orders and OrderItems tables grow monotonically as users place orders. Loading both entirely into memory to filter for one customer is O(total_orders + total_order_items) in database I/O, memory allocation, and TDS parsing — regardless of how many orders the customer has.

The `StringComparison.OrdinalIgnoreCase` comparison at line 42 forces client-side evaluation because SQL Server can handle case-insensitive comparison natively via collation. The N+1 product lookup generates a separate round-trip per line item, compounding the issue.

All queries use change-tracking (no `AsNoTracking()`), causing EF Core to create `EntityEntry` snapshots for every materialized entity — explaining the high Gen0→Gen1 promotion rate and 1.3GB peak heap.

## Proposed Fixes

1. **Server-side filtering with batch join:** Replace both `ToListAsync()` full table scans with server-side `Where()` clauses. Filter orders by `CustomerName` in SQL (the database collation handles case-insensitivity). Filter order items by the matched order IDs. Batch product lookups using `Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`. Add `AsNoTracking()` to all queries.

2. **Eliminate N+1 with dictionary lookup:** Collect all distinct `ProductId` values from the filtered order items, fetch them in a single query into a dictionary, then look up each product from the dictionary instead of individual `FindAsync` calls.

## Expected Impact

- p95 latency: ~15-30ms reduction for order page loads (from 2 full scans + N queries down to 3 filtered queries)
- RPS: ~3-5% improvement from dramatically reduced database load
- Memory: Significant heap reduction — only customer-specific rows are materialized instead of all orders/items, and AsNoTracking eliminates change-tracking overhead
- The OrderId index on OrderItems (line 58 in AppDbContext.cs) enables efficient filtered lookups
