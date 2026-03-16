# Replace materialize-then-remove with raw SQL DELETE in ClearCart

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `Controllers/CartController.cs:148-153`, the `ClearCart` endpoint materializes all cart items into memory before deleting them:

```csharp
var sessionItems = await _context.CartItems
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();                        // Round trip 1: SELECT

_context.CartItems.RemoveRange(sessionItems);
await _context.SaveChangesAsync();          // Round trip 2: DELETE per item
```

This performs two database round trips: a SELECT to load entities, then N individual DELETE statements (one per cart item) batched in SaveChanges. The CPU profile shows `SemaphoreSlim.Wait` (293 samples) indicating connection pool contention under load — every unnecessary round trip holds a connection longer and increases contention.

The k6 scenario calls `http.del(${BASE_URL}/api/cart/session/${sessionId})` every iteration, and each iteration has ~2 cart items (one from API POST at line 7 of the scenario, one from the page POST).

## Theory

Under 500 concurrent VUs, the ClearCart endpoint executes ~61 times/sec. Each call holds a database connection for two sequential operations: SELECT (fetch items) → DELETE (remove items). The SELECT is entirely wasteful since we don't need the entity data — we only need to delete rows matching the SessionId. Replacing with a single `DELETE FROM CartItems WHERE SessionId = @p` eliminates the SELECT round trip, halves connection hold time, reduces EF change tracker overhead (no entities tracked), and reduces GC pressure (no CartItem objects allocated). This directly addresses the SemaphoreSlim.Wait contention shown in the CPU profile.

## Proposed Fixes

1. **Use raw SQL DELETE:** Replace lines 148-153 with `await _context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM CartItems WHERE SessionId = {sessionId}")`. This executes a single SQL statement without materializing any entities. Return `NoContent()` as before.

## Expected Impact

- p95 latency: ~8-15ms reduction on ClearCart requests (one fewer DB round trip, no entity materialization)
- Connection pool: reduced contention under high concurrency (halved connection hold time for this endpoint)
- The ClearCart endpoint is ~5.6% of total k6 traffic. With ~10ms latency savings, overall p95 improvement is approximately 0.1%.
