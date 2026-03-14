# Root Cause Analysis — Experiment 2

> Generated: 2026-03-13 19:03:23 | Classification: narrow — The optimization fixes query filtering in ReviewsController methods by moving .Where() logic to the server-side query (before .ToListAsync()), staying within a single file with no API contract, dependency, or test changes needed.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 888.549155000001ms | 888.549155000001ms |
| Requests/sec | 683.2 | 683.2 |
| Error Rate | 0% | 0% |

---
# Review queries load entire table instead of filtering server-side

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

**GetReviewsByProduct (lines 54-55)** loads ALL reviews and filters in memory:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

**GetAverageRating (lines 70-74)** also loads ALL reviews to compute a single aggregate:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
var average = productReviews.Any()
    ? Math.Round(productReviews.Average(r => r.Rating), 2)
    : 0.0;
```

Additionally, **CreateReview (lines 95-97)** performs a wasted post-insert computation — loading ALL reviews just to compute an unused average:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating);
```

The same pattern appears in Razor pages: `Pages/Products/Detail.cshtml.cs:34` and `Pages/Index.cshtml.cs:36` also call `_context.Reviews.ToListAsync()`.

## Theory

The Reviews table is likely the largest seeded table (products × reviews-per-product). Loading every review row for every request — when only a handful match the target `ProductId` — wastes significant DB I/O, network bandwidth, and memory. The `GetAverageRating` endpoint is particularly wasteful: SQL Server can compute `AVG(Rating) WHERE ProductId = @id` in microseconds using an index scan, but instead the app transfers every review row to .NET and computes the average in C#. Under 500 concurrent VUs, the 2 review API endpoints plus 2 Razor pages all compete for the same full-table scan, creating heavy read contention.

The dead computation in `CreateReview` (line 97) adds an unnecessary full-table scan on every review creation, though this endpoint isn't in the k6 hot path.

## Proposed Fixes

1. **Push WHERE to SQL for GetReviewsByProduct:** Replace `_context.Reviews.ToListAsync()` + client-side `.Where()` with `_context.Reviews.Where(r => r.ProductId == productId).ToListAsync()`.

2. **Use server-side aggregate for GetAverageRating:** Replace the full-table load with `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` and `CountAsync()`. This translates to a single SQL `SELECT AVG(Rating), COUNT(*) WHERE ProductId = @id`.

3. **Remove dead computation in CreateReview:** Delete lines 95-97 which load all reviews and compute an unused average after every insert.

## Expected Impact

- **p95 latency:** GetAverageRating should drop ~60-100ms (server-side aggregate vs. full scan + client computation). GetReviewsByProduct should drop ~50-80ms.
- **Memory/GC:** Large reduction in per-request allocations — only matching reviews materialized instead of entire table.
- **Overall p95 improvement:** ~5-8% (estimated ~70ms average reduction across ~15% of traffic).

