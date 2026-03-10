# Root Cause Analysis — Experiment 4

> Generated: 2026-03-10 00:04:48 | Classification: narrow — All optimizations (removing N+1 queries, adding `.Where()` clauses, using `.Include()` for eager loading, replacing full table scans with filtered queries) can be implemented entirely within this single controller file by modifying query logic and DbContext calls without adding dependencies or changing API contracts.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 583.033435ms | 888.549155000001ms |
| Requests/sec | 978.6 | 683.2 |
| Error Rate | 0% | 0% |

---
# Fix full table scans and N+1 queries in OrdersController

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:35-37`, `GetOrdersByCustomer` loads the entire Orders table into memory and filters client-side:

```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At `OrdersController.cs:52-58`, `GetOrder` loads ALL OrderItems into memory, filters client-side, then does N+1 product lookups:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();
// ...
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

At `OrdersController.cs:25`, `GetOrders` materializes all orders with change tracking enabled:

```csharp
var orders = await _context.Orders.ToListAsync();
```

At `OrdersController.cs:103-118`, `CreateOrder` (hit every k6 iteration) does per-item `FindAsync` calls — an N+1 pattern for product validation:

```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
```

The CPU profile confirms SQL data reading dominates: TdsParserStateObject.TryReadChar (1.7%), UnicodeEncoding.GetCharCount+GetChars (1.73%), and EF Core materialization type-casting (2.72%) — all driven by over-fetching rows.

## Theory

`GetOrdersByCustomer` transfers the entire Orders table over the wire from SQL Server, then discards most rows in C#. As the k6 test creates hundreds of orders (POST /api/orders is called every VU iteration), this table grows during the test, making each subsequent GET progressively slower.

`GetOrder` compounds two anti-patterns: a full OrderItems table scan plus a sequential database round-trip per item for product details. With the Orders table growing during load testing, the OrderItems table grows even faster (2-3 items per order), amplifying the cost.

`CreateOrder` is the most impactful for the baseline k6 scenario since it's called every iteration by every VU. Each call does N individual `FindAsync` round trips (N=2 in the k6 test) plus two `SaveChangesAsync` calls (line 99 and 122). That's 4 DB round trips per request. Under 500 VUs, this creates massive DB connection contention.

All read endpoints lack `AsNoTracking()`, adding EF Core change-tracking overhead (StateManager.StartTrackingFromQuery appears in the CPU profile with ~3K samples) for entities that are never modified.

## Proposed Fixes

1. **Server-side filtering + AsNoTracking for reads:** In `GetOrdersByCustomer` (line 35), replace `ToListAsync()` + client-side filter with `.AsNoTracking().Where(o => o.CustomerName == customerName).ToListAsync()`. In `GetOrder` (line 52), replace the full OrderItems scan with `.AsNoTracking().Where(i => i.OrderId == id).ToListAsync()`. Add `.AsNoTracking()` to `GetOrders` (line 25). Replace the N+1 product lookups in `GetOrder` with a batch query: collect all ProductIds, then fetch products in one query with `.Where(p => productIds.Contains(p.Id))`.

2. **Batch product lookup in CreateOrder:** At lines 103-118, collect all `ProductId` values from `request.Items`, fetch them in a single query using `.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`, then look up from the dictionary in the loop. This reduces N+1 round trips to a single query.

## Expected Impact

- p95 latency: ~5-10% reduction. CreateOrder drops from 4 DB round trips to 2 (one batch product lookup + one SaveChanges). GetOrdersByCustomer and GetOrder go from full-table-scan + N+1 to indexed single queries, though these endpoints aren't in the baseline k6 test.
- RPS: ~5-8% improvement from reduced DB connection contention on the CreateOrder hot path.
- GC pressure: Moderate reduction — fewer entities materialized means less allocation churn, helping the 10.4% GC pause ratio.

