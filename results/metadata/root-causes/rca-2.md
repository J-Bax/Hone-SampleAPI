# Eliminate redundant product existence DB round trips in review endpoints

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:49-56`, `GetReviewsByProduct` executes two sequential database queries:

```csharp
var productExists = await _context.Products.AsNoTracking().AnyAsync(p => p.Id == productId); // Query 1
if (!productExists)
    return NotFound(...);

var filtered = await _context.Reviews.AsNoTracking()  // Query 2
    .Where(r => r.ProductId == productId)
    .ToListAsync();
```

At `ReviewsController.cs:67-76`, `GetAverageRating` has the same pattern:

```csharp
var productExists = await _context.Products.AsNoTracking().AnyAsync(p => p.Id == productId); // Query 1
if (!productExists)
    return NotFound(...);

var stats = await _context.Reviews  // Query 2
    .Where(r => r.ProductId == productId)
    .GroupBy(r => r.ProductId)
    .Select(g => new { Count = g.Count(), Average = g.Average(r => (double)r.Rating) })
    .FirstOrDefaultAsync();
```

In both cases, the product existence check (Query 1) is a separate DB round trip that is redundant when reviews exist — if reviews reference the productId, the product necessarily exists.

The CPU profiler shows DI/service resolution at 0.73% and connection-related overhead in the hot path. Each unnecessary round trip consumes a SQL connection from the pool, and under 500 VUs this creates contention.

## Theory

These two endpoints together represent ~11.1% of total k6 traffic (each called once per iteration). Each call makes 2 DB round trips when 1 would suffice in the common case. The k6 scenario uses `seededId(500, 2)` for review product IDs, targeting products 1–500 which all have seeded reviews (1–7 each per `SeedData.cs:86-88`).

In the common case (product exists AND has reviews), the existence check is pure overhead — the reviews query result implicitly proves the product exists. Only when no reviews are found do we need to distinguish between "product exists but has no reviews" (return empty list/zero average) vs "product doesn't exist" (return 404).

With ~2,420 DB operations per second under peak load, eliminating 2 redundant round trips per iteration (~116 ops/sec) reduces total DB operation count by ~4.8%, easing SQL connection pool pressure and reducing queuing delays that contribute to p95.

## Proposed Fixes

1. **Reorder queries to make existence check conditional**: In `GetReviewsByProduct`, execute the reviews query first. If results are non-empty, return them directly (no existence check needed). Only if the result is empty, perform the `AnyAsync` product existence check to determine whether to return 404 or empty list:

   ```
   Reviews query first → if has results → return Ok(results)
                        → if empty → check product exists → 404 or empty Ok([])
   ```

   Apply the same pattern to `GetAverageRating`: run the aggregate first, check product existence only when the aggregate returns null.

2. **Preserve 404 behavior**: The fix must still return 404 for non-existent products. The conditional check ensures this contract is maintained while eliminating the round trip for the ~95%+ of calls where reviews exist.

## Expected Impact

- **p95 latency**: ~1–2ms reduction per request (one saved round trip to LocalDB)
- **Connection pool**: ~4.8% fewer DB operations under peak load, reducing pool exhaustion risk
- **Overall p95**: ~0.3–0.5% improvement from combined direct savings and connection pool relief
