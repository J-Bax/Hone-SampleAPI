# Root Cause Analysis — Experiment 20

> Generated: 2026-03-15 22:05:37 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 537.060355ms | 7546.103045ms |
| Requests/sec | 1055.8 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add AsNoTracking and server-side filtering to GetOrder with batched product lookup

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:52-53`, `GetOrder` loads the **entire** OrderItems table into memory, then filters client-side:

```csharp
var allItems = await _context.OrderItems.ToListAsync();  // Full table scan, tracked
var items = allItems.Where(i => i.OrderId == id).ToList();
```

At `OrdersController.cs:56-58`, it then performs N+1 product lookups with tracked `FindAsync`:

```csharp
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

At `OrdersController.cs:35-37`, `GetOrdersByCustomer` loads ALL orders and filters client-side despite an existing `CustomerName` index (`AppDbContext.cs:52`):

```csharp
var allOrders = await _context.Orders.ToListAsync();  // Full table scan, tracked
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At `OrdersController.cs:25`, `GetOrders` lacks `AsNoTracking`:

```csharp
var orders = await _context.Orders.ToListAsync();  // Tracked unnecessarily
```

The OrderItems table grows continuously during the load test (~232 inserts/sec from order creation). By test end, it may contain 15,000+ rows. Each `GetOrder` call materializes all of them.

## Theory

While these read endpoints are not directly called in the k6 scenario, `CreateOrder` (line 129) returns `CreatedAtAction(nameof(GetOrder), ...)`. Though k6 doesn't follow the Location header, any production consumer or API explorer exercising the full CRUD surface would hit `GetOrder`. More critically:

1. **Tracked entities accumulate**: `GetOrders` and `GetOrdersByCustomer` load tracked entities into the DbContext's identity map. Since DbContext is request-scoped, this doesn't leak across requests, but the per-request overhead includes snapshot creation and change tracker bookkeeping for entities that are only read.

2. **Growing table amplifies cost**: The OrderItems table grows throughout the test. The full-scan in `GetOrder` (line 52) becomes progressively more expensive. Even if not in the k6 hot path now, this is a ticking time bomb for any future scenario that includes order detail viewing.

3. **The N+1 in GetOrder** (lines 56-58) issues a separate `FindAsync` per order item, each tracked. With 1–5 items per order, that's 1–5 extra round trips.

Note: Experiment 14 attempted a similar optimization and was measured as "stale." That may have been because the measurement scenario didn't exercise these endpoints. The code improvements are still objectively correct and prevent worst-case degradation as data grows.

## Proposed Fixes

1. **GetOrdersByCustomer (lines 35-37)**: Replace client-side filter with server-side `Where` using the existing `CustomerName` index, and add `AsNoTracking`:
   - Use `.AsNoTracking().Where(o => o.CustomerName == customerName)` (case-insensitive comparison can rely on SQL Server's default collation).

2. **GetOrder (lines 52-68)**: Replace full OrderItems scan with `.AsNoTracking().Where(i => i.OrderId == id)` using the `OrderId` index. Replace the N+1 product loop with a batch lookup: collect productIds, use `.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`.

3. **GetOrders (line 25)**: Add `.AsNoTracking()` to eliminate change tracking overhead.

## Expected Impact

- **p95 latency**: Minimal immediate impact on current k6 scenario since these endpoints aren't in the hot path
- **Allocation reduction**: Eliminates tracked entity overhead for read-only order queries, reducing per-request GC pressure for any caller
- **Resilience**: Prevents progressive degradation as OrderItems table grows during extended load tests
- **Overall p95**: ~0.1–0.3% improvement from reduced DbContext overhead and background allocation pressure

