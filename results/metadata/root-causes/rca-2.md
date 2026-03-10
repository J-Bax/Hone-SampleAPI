# Eliminate redundant SaveChanges round-trip in CreateOrder

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** architecture

## Evidence

At `OrdersController.cs:107-108`, `CreateOrder` performs a first `SaveChangesAsync` solely to obtain the auto-generated `order.Id`:

```csharp
_context.Orders.Add(order);
await _context.SaveChangesAsync(); // Save to get order ID
```

Then at line 132, order items are added individually in a loop:

```csharp
_context.OrderItems.Add(orderItem);
```

Finally at line 136, a second `SaveChangesAsync` persists the items and updates the total:

```csharp
await _context.SaveChangesAsync();
```

The k6 scenario creates an order every iteration (`baseline.js:94-106`) with 2 items, meaning every VU executes 2 database round-trips per iteration for order creation. At 500 VUs, that is 1,000 round-trips per iteration cycle dedicated to order creation alone.

With average CPU at only 18.66%, the server is not CPU-bound — it is spending significant time waiting on I/O (database round-trips). Each `SaveChangesAsync` acquires a connection from the pool, executes SQL, and returns. The default SQL Server connection pool size is 100; with 500 VUs each needing 2 round-trips for orders plus round-trips for other endpoints, connection pool contention inflates wait times.

## Theory

The dual `SaveChangesAsync` pattern doubles the connection hold-time for order creation requests. Under high concurrency, connection pool saturation creates a queuing effect: requests block waiting for an available connection, and every extra round-trip lengthens the queue. Since the k6 scenario fires 13 requests per iteration and order creation is the only write-heavy endpoint with 2 round-trips, it disproportionately contributes to connection pool pressure.

The 18.66% average CPU with a 408ms p95 latency confirms the bottleneck is I/O wait, not computation. Reducing database round-trips directly reduces the time each request holds a pooled connection, freeing capacity for concurrent requests across all endpoints.

## Proposed Fixes

1. **Add a navigation property `Items` to the `Order` model** (`Models/Order.cs`): Add `public ICollection<OrderItem> Items { get; set; }` and configure the relationship in `AppDbContext.OnModelCreating`. Then refactor `CreateOrder` to build the full object graph — set `orderItem.Order = order` (or add to `order.Items`) — and call `SaveChangesAsync()` once. EF Core will insert the Order, retrieve the generated ID via `SCOPE_IDENTITY()`, then batch-insert all OrderItems with the correct `OrderId` in a single transaction.

## Expected Impact

- **p95 latency**: ~3-5% improvement. Eliminating one database round-trip per order reduces connection hold-time by ~50% for this endpoint, easing pool contention across all concurrent requests.
- **RPS**: ~2-4% improvement from reduced I/O wait and connection pool pressure.
- **Error rate**: No change (0% baseline).
- This is a secondary optimization — less impactful than result-set limiting but still measurable under sustained high concurrency (500 VUs).
