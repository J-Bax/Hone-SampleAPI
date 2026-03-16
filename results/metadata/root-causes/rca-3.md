# Consolidate redundant SaveChangesAsync round trips in checkout post

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

In `Checkout/Index.cshtml.cs`, the `OnPostAsync` method performs **4 separate `SaveChangesAsync` calls** (lines 81, 106, 109, 112):

```csharp
await _context.SaveChangesAsync(); // Line 81: save order to get ID

// ... add order items in loop ...

await _context.SaveChangesAsync(); // Line 106: save order items

order.TotalAmount = Math.Round(total, 2);
await _context.SaveChangesAsync(); // Line 109: update order total

_context.CartItems.RemoveRange(sessionItems);
await _context.SaveChangesAsync(); // Line 112: clear cart
```

Lines 106, 109, and 112 are three separate database round trips for operations that could be batched into a single call. The first save (line 81) is required to obtain the order's auto-generated ID, but the remaining three are independently ordered only by the code structure, not by a database dependency.

## Theory

Each `SaveChangesAsync` is a full database round trip: acquire a connection from the pool, send the SQL command batch, wait for acknowledgement, return the connection. Under 500 concurrent VUs:

1. **Connection pool pressure**: 3 round trips where 1 would suffice means the checkout path holds a connection 3× longer than necessary during the item/total/cart-clear phase.
2. **Transaction overhead**: Each `SaveChangesAsync` wraps its changes in an implicit transaction, so there are 3 separate transaction commits instead of 1, with associated log flushes.
3. **Cascading contention**: Longer connection hold times increase queue depth in the ADO.NET connection pool, adding wait time to all other concurrent requests.

This is a **different issue** from the N+1 and per-item SaveChanges that experiment 3 addressed (which was about saves inside a loop). The current code correctly batches item additions but still commits them across 3 redundant round trips.

## Proposed Fixes

1. **Move `order.TotalAmount` assignment before the items save**: Calculate and set `order.TotalAmount = Math.Round(total, 2)` immediately after the foreach loop (before line 106). EF Core will include the UPDATE in the same batch as the OrderItem INSERTs.

2. **Move `RemoveRange` before the save**: Call `_context.CartItems.RemoveRange(sessionItems)` before the single remaining `SaveChangesAsync`. EF Core will include the DELETE statements in the same command batch.

3. **Result**: Lines 106, 109, and 112 collapse into a single `SaveChangesAsync` that persists order items, updates the order total, and clears the cart atomically.

## Expected Impact

- **Per-request latency**: ~20ms reduction (eliminating 2 DB round trips and their associated transaction commits).
- **Overall p95**: Small direct improvement given 5.6% traffic share, but reduces connection pool hold time by ~67% for the checkout path's second phase.
- **Reliability**: Atomic commit of items + total + cart-clear is actually more correct — the current code can leave inconsistent state if the process crashes between saves.
