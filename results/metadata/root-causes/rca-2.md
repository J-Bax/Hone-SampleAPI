# Eliminate full-table review loads and wasted computation

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54-55`, `GetReviewsByProduct` loads **all ~2000 reviews** into memory then filters by ProductId:

```csharp
var allReviews = await _context.Reviews.ToListAsync();              // line 54
var filtered = allReviews.Where(r => r.ProductId == productId).ToList(); // line 55
```

At `ReviewsController.cs:70-71`, `GetAverageRating` repeats the same pattern:

```csharp
var allReviews = await _context.Reviews.ToListAsync();              // line 70
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList(); // line 71
```

At `ReviewsController.cs:95-97`, `CreateReview` loads all reviews after inserting, computes an average, and discards the result:

```csharp
var allReviews = await _context.Reviews.ToListAsync();              // line 95
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList(); // line 96
var _ = productReviews.Average(r => r.Rating);                     // line 97 — wasted
```

The k6 baseline scenario hits `GET /api/reviews/by-product/{id}` and `GET /api/reviews/average/{id}` every iteration (baseline.js lines 65, 70). With ~2000 Review entities (each with a `Comment` string up to 2000 chars), this adds substantial data transfer and allocation.

## Theory

Each VU iteration loads the entire Reviews table twice (by-product + average endpoints). With ~2000 reviews × 2 = 4000 Review entities per iteration, at ~50 iterations/sec, that's ~200,000 Review entities/sec materialized from SQL, each carrying a Comment string (avg ~80 chars). This contributes heavily to the 12% CPU in TDS parsing, the 2.7% in Unicode decoding, and the 1.9 GB/sec allocation rate.

The `GetAverageRating` endpoint is particularly wasteful — it materializes 2000 entities just to compute a single scalar (average rating) that SQL Server could compute directly with `AVG()`. The `CreateReview` POST has a completely wasted computation that loads all reviews for no purpose.

## Proposed Fixes

1. **Server-side filtering:** In `GetReviewsByProduct` (line 54-55), replace with `_context.Reviews.Where(r => r.ProductId == productId).AsNoTracking().ToListAsync()`. In `GetAverageRating` (line 70-75), replace with a server-side aggregate: `await _context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => (double?)r.Rating) ?? 0.0` and `await _context.Reviews.CountAsync(r => r.ProductId == productId)`. This pushes both filter and aggregation to SQL.

2. **Remove wasted computation in CreateReview:** Delete lines 95-97 entirely — they load all reviews, compute an average, and discard it. Also add `.AsNoTracking()` to the `GetReviews` endpoint (line 25).

## Expected Impact

- **p95 latency:** ~15-20% reduction. Two full-table scans per iteration eliminated; the average endpoint becomes a single SQL `AVG()` query returning one number instead of 2000 rows.
- **RPS:** ~15-20% increase from reduced SQL round-trip data and lower allocation pressure.
- **GC pressure:** Materilizing ~4-10 reviews (for a specific product) instead of 2×2000 reduces per-request allocations by ~100-400x for these endpoints. The CreateReview endpoint drops one full table scan entirely.
- **Error rate:** Should remain at 0%.
