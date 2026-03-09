# Client-side evaluation loads entire Reviews table on every request

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54-55`, `GetReviewsByProduct` materializes the entire Reviews table (~2000+ rows) into memory and then filters by ProductId in C#:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

The identical pattern appears at `ReviewsController.cs:70-71` in `GetAverageRating`:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

Additionally, `CreateReview` at lines 95-97 performs a completely wasted full-table materialization after every insert:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating);
```

The k6 baseline scenario calls both `GET /api/reviews/by-product/{id}` and `GET /api/reviews/average/{id}` on every VU iteration (lines 65-73 of baseline.js). At 500 VUs firing back-to-back, this materializes ~2000 Review objects twice per iteration, producing massive allocation volume that directly drives the observed 162 GB total allocations, inverted GC generation profile (107 Gen2 vs 8 Gen0), and 12.4% GC pause ratio.

## Theory

Each `ToListAsync()` on the full Reviews table forces EF Core to: (1) execute an unfiltered `SELECT *` query, (2) allocate a managed object for every row (~2000+ Review entities), (3) populate the DbContext change tracker with all entities. With two such calls per VU iteration at 500 concurrent VUs, the allocation rate overwhelms the Gen0 GC budget. Objects survive to Gen1/Gen2 because the DbContext (scoped per request) keeps them rooted until the request completes, and concurrent requests overlap. This explains the inverted generation counts—the GC cannot reclaim objects fast enough in ephemeral collections, forcing constant full-heap Gen2 sweeps at 1.25/sec, each pausing the application for up to 192ms and directly inflating p95 latency to 813ms.

The `GetAverageRating` method is especially wasteful because it materializes thousands of objects just to compute a single scalar that SQL Server can compute natively with `AVG()`.

## Proposed Fixes

1. **Push Where clause to SQL in GetReviewsByProduct:** Replace the two-step load-then-filter with `_context.Reviews.Where(r => r.ProductId == productId).ToListAsync()`. This sends `WHERE ProductId = @p0` to SQL Server, returning only the handful of matching rows.

2. **Use server-side aggregate in GetAverageRating:** Replace the full materialization with `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` and `.CountAsync()`. This computes the average in SQL, returning a single scalar instead of thousands of objects.

3. **Remove wasted query in CreateReview:** Delete lines 95-97 entirely—the computed average is assigned to a discard variable and never used.

## Expected Impact

- **Allocation volume:** Eliminates ~4000 Review entity allocations per VU iteration (the two full-table loads), reducing total allocations by an estimated 40-50%. This should dramatically reduce Gen2 collection frequency and GC pause ratio.
- **p95 latency:** Expected reduction of 30-45% (from ~813ms toward ~450-570ms) due to reduced GC pauses and faster SQL queries (returning 1-7 rows instead of 2000+).
- **RPS:** Expected increase of 25-40% as GC stop-the-world pauses decrease and database round-trip time drops.
