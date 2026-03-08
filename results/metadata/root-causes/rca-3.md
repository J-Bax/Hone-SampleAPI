# Review endpoints load all reviews into memory instead of filtering server-side

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54-55`, `GetReviewsByProduct` loads ALL reviews then filters in C#:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

At lines 70-71, `GetAverageRating` does the same full scan for a simple aggregation:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

At lines 95-97, `CreateReview` re-loads ALL reviews after insert just to compute an average that is never used:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating);
```

The `stress.js` scenario calls both `/api/reviews/by-product/{id}` and `/api/reviews/average/{id}` (lines 41-42).

## Theory

With seed data populating reviews for many products, every review endpoint request transfers the full Reviews table from SQL Server to the app. For `GetAverageRating`, SQL Server can compute `AVG(Rating) WHERE ProductId = @id` entirely on the server in a single index scan, returning just two numbers instead of thousands of rows. The wasted `ToListAsync` in `CreateReview` is pure overheadΓÇöit loads every review, computes an unused average, then discards both.

Under 200 VUs, the Reviews table is read-heavy and likely one of the larger tables (many products ├ù multiple reviews each), so the full-scan pattern contributes meaningfully to overall latency and memory pressure.

## Proposed Fixes

1. **Server-side filtering for `GetReviewsByProduct`:** Replace with `_context.Reviews.Where(r => r.ProductId == productId).ToListAsync()`.

2. **Server-side aggregation for `GetAverageRating`:** Replace with `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` and `.CountAsync()`, or use a `GroupBy` projection so SQL returns just the aggregate.

3. **Remove dead code in `CreateReview`:** Delete lines 95-97 entirelyΓÇöthe loaded reviews and computed average are assigned to a discard variable and never used.

## Expected Impact

- p95 latency: **10-15% reduction** ΓÇö Review endpoints appear in the stress mix and the full table scan is eliminated.
- RPS: **5-10% improvement** ΓÇö Reduced memory allocations and DB I/O free capacity for other concurrent requests.
- Error rate: Remains at 0%.
