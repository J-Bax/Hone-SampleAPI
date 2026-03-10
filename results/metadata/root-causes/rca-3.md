# Full table scans, N+1, and per-item saves in Checkout

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Checkout/Index.cshtml.cs:61`, the checkout POST loads the entire CartItems table:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();
```

At line 86, N+1 product lookups occur inside a loop:

```csharp
foreach (var cartItem in sessionItems)
{
    var product = await _context.Products.FindAsync(cartItem.ProductId);
```

At line 99, `SaveChangesAsync()` is called per order item:

```csharp
    _context.OrderItems.Add(new OrderItem { ... });
    total += price * cartItem.Quantity;
    await _context.SaveChangesAsync();
}
```

At lines 106-110, cart cleanup also calls `SaveChangesAsync()` per item:

```csharp
foreach (var cartItem in sessionItems)
{
    _context.CartItems.Remove(cartItem);
    await _context.SaveChangesAsync();
}
```

The `LoadCartSummary()` method (line 120) repeats the same full table scan + N+1 pattern.

## Theory

Checkout is the most write-intensive page: it creates an order, adds order items, and clears the cart. The current implementation generates 1 (full cart scan) + N (product lookups) + N (per-item order saves) + N (per-item cart deletes) = 3N+1 database round-trips for a cart with N items. Each `SaveChangesAsync()` is a separate transaction with its own round-trip, connection acquisition, and commit overhead.

The full CartItems table scan in `LoadCartSummary()` runs on GET as well as being duplicated in POST, doubling the impact. The N+1 product lookup pattern forces sequential execution — each query awaits before the next, eliminating any pipeline parallelism.

Per the memory profile, the 623 MB/sec allocation rate is partly driven by these unnecessary materializations and per-item async state machines.

## Proposed Fixes

1. **Server-side filtering and batch product lookup:** Replace `CartItems.ToListAsync()` with `CartItems.Where(c => c.SessionId == sessionId).ToListAsync()`. Batch product lookups using `Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)` instead of per-item `FindAsync`.

2. **Batch all writes into single SaveChanges:** Add all `OrderItem` entities to the context in the loop but call `SaveChangesAsync()` only once after the loop. Replace per-item cart removal with `RemoveRange()` followed by a single `SaveChangesAsync()`. This collapses 2N write round-trips into 2.

## Expected Impact

- p95 latency: ~20-40ms reduction for checkout operations (from 3N+1 round-trips to ~4 queries total)
- RPS: ~2-4% improvement, especially under concurrent checkout load
- Memory: Reduced allocations from eliminating full-table materialization and per-item async overhead
- Write throughput improves significantly since batch saves reduce transaction commit overhead
