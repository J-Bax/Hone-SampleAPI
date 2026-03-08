# Root Cause Analysis — Experiment 3

> Generated: 2026-03-09 04:39:13 | Classification: narrow — All N+1 queries and full table loads can be fixed by adding `.Include()` and `.Where()` clauses directly in this controller file's query methods without modifying other files, adding dependencies, changing migrations, or altering API contracts.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 1745.51ms | 1934.079535ms |
| Requests/sec | 341.1 | 309.4 |
| Error Rate | 0% | 0% |

---
# Orders endpoints load full tables and N+1 query products

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:35-37`, `GetOrdersByCustomer` loads the entire Orders table into memory and filters in C#:

```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At `OrdersController.cs:52-53`, `GetOrder` loads ALL OrderItems into memory then filters client-side:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();
```

At `OrdersController.cs:56-58`, `GetOrder` then issues a separate SQL query per order item to fetch the product (classic N+1):

```csharp
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

With ~100 orders and ~250 order items seeded (`SeedData.cs:113-146`), every `GetOrder` call loads all 250 order items plus issues 1-5 individual product lookups. `GetOrdersByCustomer` loads all 100 orders for every request.

At `OrdersController.cs:103-106`, `CreateOrder` also performs N+1 product lookups:

```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
```

## Theory

`GetOrdersByCustomer` transfers all 100 orders from SQL to the app server on every call, then discards most of them. Under concurrent load, this wastes both database I/O and GC pressure.

`GetOrder` is worse: it transfers all ~250 OrderItem rows, filters to the few matching the order, then issues 1-5 sequential `FindAsync` calls ΓÇö each a separate database round-trip. Under load with k6, this serialized N+1 pattern creates a latency bottleneck proportional to the number of items per order, compounded by connection pool contention.

`CreateOrder` has the same N+1 pattern for product lookups, plus two `SaveChangesAsync` calls (lines 99 and 122) where one would suffice.

## Proposed Fixes

1. **Server-side filtering for GetOrdersByCustomer:** Replace the `ToListAsync()` + LINQ-to-Objects with a server-side `Where` clause using `EF.Functions.Like` or `ToLower()` for case-insensitive matching at `OrdersController.cs:35-37`.

2. **Join-based query for GetOrder:** Replace the full OrderItems table load + N+1 product lookups (lines 52-68) with a single query that joins OrderItems and Products filtered by OrderId, e.g., `_context.OrderItems.Where(i => i.OrderId == id).Join(_context.Products, ...)` similar to the pattern already used in `CartController.cs:25-41`.

3. **Batch product lookup for CreateOrder:** At lines 103-118, collect all ProductIds upfront, load them in a single `Where(p => productIds.Contains(p.Id))` query, then iterate the in-memory dictionary. Remove the first `SaveChangesAsync` at line 99 by using a transaction or deferring the order ID assignment.

## Expected Impact

- p95 latency: ~15-25% reduction. The N+1 in `GetOrder` adds multiple sequential round-trips under load; eliminating them should significantly reduce tail latency.
- RPS: ~15-20% increase. Fewer database round-trips per request means fewer connections held, reducing pool contention.
- Error rate: No change (currently 0%).

