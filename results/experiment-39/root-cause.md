# Root Cause Analysis — Experiment 39

> Generated: 2026-03-16 21:30:06 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 513.94764ms | 7546.103045ms |
| Requests/sec | 1166.3 | 125.5 |
| Error Rate | 0% | 0% |

---
# Consolidate two SaveChangesAsync into one in CreateOrder

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:108-109`, the order is saved solely to obtain its generated `Id`:

```csharp
_context.Orders.Add(order);
await _context.SaveChangesAsync(); // Save to get order ID
```

Then at line 134, each `OrderItem` is added using `order.Id`, and a second save is issued at line 138:

```csharp
_context.OrderItems.Add(orderItem);
// ...
await _context.SaveChangesAsync();
```

Every `POST /api/orders` call (every k6 iteration) pays two DB round trips. Under 500 VUs at ~65 iterations/sec, that's ~65 unnecessary round trips/sec to LocalDB.

The CPU profile shows SQL Server engine work at 12.7% inclusive — every saved round trip reduces contention on the SQL connection pool and frees SQL engine time for other concurrent requests.

## Theory

EF Core 6 assigns a temporary negative key value when you call `_context.Orders.Add(order)` on an entity with a store-generated identity column. If you then set `orderItem.OrderId = order.Id` (which holds the temp value) and add the OrderItem to the context, EF recognises the temp-key dependency. On a single `SaveChangesAsync()`, EF inserts the Order first, retrieves the real generated `Id`, patches the FK in each `OrderItem`, and inserts them — all within one implicit transaction.

The current two-save pattern was necessary before EF's temp-key fixup was leveraged, but it doubles the round-trip cost of every order creation for no benefit.

## Proposed Fixes

1. **Remove the first SaveChangesAsync (line 109).** Keep `_context.Orders.Add(order)` so EF assigns a temp key. Move the product lookup and OrderItem creation loop to execute before any save. After all OrderItems are added (using `order.Id` which now holds the temp key), call a single `SaveChangesAsync()` at the end. The `order.Id` property will be updated in-place with the real generated value after SaveChanges, so the `CreatedAtAction` response at line 140 still returns the correct ID.

## Expected Impact

- p95 latency: ~10-15ms reduction per CreateOrder request (one fewer LocalDB round trip)
- RPS: slight improvement from reduced DB connection pool contention
- Indirect benefit: freeing ~65 round trips/sec under peak load reduces SQL Server contention for all concurrent requests, amplifying the per-request savings
- Overall p95 improvement: ~1-2%

