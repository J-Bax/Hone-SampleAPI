# GetAverageRating makes 3 separate database round-trips per request

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:65-73`, `GetAverageRating` executes 3 sequential database queries:

```csharp
// Query 1 (line 65): Check product exists
var productExists = await _context.Products.AnyAsync(p => p.Id == productId);

// Query 2 (line 70): Count reviews
var reviewCount = await productReviewsQuery.CountAsync();

// Query 3 (line 72): Average rating  
var average = reviewCount > 0
    ? Math.Round(await productReviewsQuery.AverageAsync(r => r.Rating), 2)
    : 0.0;
```

The k6 scenario calls this endpoint once per VU iteration (`/api/reviews/average/{reviewProductId}`), representing 5.6% of total traffic. Under 500 VUs at ~79 requests/second, this generates ~237 SQL query executions/second just for this endpoint.

The CPU profile shows `StateSnapshot.Snap` (0.8%) and `PrepareAsyncInvocation` (0.6%) — async infrastructure overhead that scales linearly with the number of awaited DB calls.

## Theory

Each database round-trip incurs connection acquisition from the pool, SQL compilation/execution, result deserialization, and async state machine overhead (snapshot, prepare, clear). At 500 concurrent VUs, the 3 sequential queries hold a DB connection approximately 3× longer than necessary per request, reducing effective connection pool throughput and increasing queuing latency for other requests.

The existence check (`AnyAsync` on Products by primary key) and the two aggregate queries (`CountAsync` + `AverageAsync` on Reviews by ProductId) can all be answered in a single SQL round-trip using a grouped projection. This reduces per-request connection hold time by ~66% and eliminates 2 async state machine cycles.

## Proposed Fixes

1. **Single aggregate query:** Replace the 3 queries with one grouped projection that returns count and average in a single round-trip:
   ```csharp
   var stats = await _context.Reviews
       .Where(r => r.ProductId == productId)
       .GroupBy(r => r.ProductId)
       .Select(g => new { Count = g.Count(), Average = g.Average(r => r.Rating) })
       .FirstOrDefaultAsync();
   ```
   When `stats` is null (no reviews), check product existence with a single `AnyAsync` to preserve the 404 behavior. When `stats` is non-null, the product implicitly exists (reviews reference it), so no extra query is needed. This reduces the common case from 3 queries to 1, and the no-reviews case from 3 to 2.

## Expected Impact

- p95 latency: ~10-15ms reduction per request from eliminating 2 DB round-trips and their async overhead
- Connection pool: ~66% fewer connections held per request for this endpoint, reducing queuing under high concurrency
- Overall p95 improvement: estimated 1.5-2% from direct latency savings and reduced connection pool contention at 500 VUs
