# Root Cause Analysis — Experiment 12

> Generated: 2026-03-14 17:11:26 | Classification: narrow — All N+1 and full table scan issues can be fixed by adding `.Include()` and `.Where()` clauses to EF Core queries within this single controller file, without modifying dependencies, endpoints, or tests.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 486.522875ms | 2054.749925ms |
| Requests/sec | 1301.3 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# OrdersController has N+1 queries and full table scans across multiple endpoints

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `Controllers/OrdersController.cs:103-107`, `CreateOrder` iterates over each order item and calls `FindAsync` individually:

```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
    if (product == null)
        continue;
```

The k6 scenario sends 2 items per order (`items: [{ productId: seededId(100, 3), quantity: 1 }, { productId: seededId(100, 4), quantity: 2 }]`), causing 2 sequential DB round-trips per request.

At line 35, `GetOrdersByCustomer` loads the entire Orders table and filters client-side:

```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At lines 52-58, `GetOrder` loads ALL OrderItems and then does N+1 product lookups:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();
...
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

A previous fix attempt (experiment 2) resulted in a **build failure**, so all these issues remain unfixed in the current code.

## Theory

The `CreateOrder` endpoint is called at ~5.5% of total traffic (~72 requests/sec under 500 VUs). Each request holds a DB connection for 4 sequential round-trips (2× `FindAsync` + 2× `SaveChangesAsync`). Batching the product lookups into one query eliminates 1 round-trip, reducing connection hold time by ~25%. Under high concurrency with connection pool contention, shorter connection hold times have compounding benefits — less queuing means lower tail latency for ALL endpoints sharing the pool.

During the 2-minute k6 test, thousands of orders and order items are created. The `GetOrder` full scan of `OrderItems.ToListAsync()` (line 52) materializes an ever-growing table. The `GetOrdersByCustomer` full scan of `Orders.ToListAsync()` (line 35) likewise degrades as orders accumulate. While these GET endpoints aren't directly in the k6 scenario, fixing them is essential for correctness and future-proofing, and the DB contention from `CreateOrder` affects system-wide throughput.

## Proposed Fixes

1. **Batch product lookups in CreateOrder:** Replace the per-item `FindAsync` loop (lines 103-107) with a single batched query — collect all `ProductId` values from `request.Items`, fetch with `.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`, then build order items from the dictionary.

2. **Fix GetOrdersByCustomer:** Replace the full table scan on line 35 with a server-side `Where` filter: `_context.Orders.Where(o => o.CustomerName == customerName).ToListAsync()`.

3. **Fix GetOrder:** Replace the full OrderItems scan on line 52 with `_context.OrderItems.Where(i => i.OrderId == id).ToListAsync()` and batch product lookups via a join or `.Where(p => productIds.Contains(p.Id))`.

## Expected Impact

- p95 latency: ~10-15ms reduction for CreateOrder by eliminating 1 DB round-trip and reducing connection hold time
- RPS: Improved DB connection pool utilization benefits all concurrent requests
- Overall: ~1-1.5% p95 improvement from CreateOrder traffic; read endpoint fixes provide resilience against table growth

