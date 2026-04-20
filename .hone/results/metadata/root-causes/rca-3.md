# Reviews endpoints load all reviews into memory and perform unnecessary post-create query

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54-55`, `GetReviewsByProduct` loads every review then filters in memory:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

The same pattern at lines 70-71 in `GetAverageRating`:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

At lines 95-97 in `CreateReview`, after saving the review, all reviews are re-loaded just to compute an average that is never returned:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating);
```

## Theory

Review endpoints are hit 4 times per VU iteration (~20% of traffic): POST create, GET by-product, GET average, DELETE. Each of the read endpoints loads the entire Reviews table. As reviews accumulate during the test, these full-table loads become increasingly expensive. The wasted re-query in `CreateReview` adds an unnecessary full-table scan to every review creation. Using server-side `WHERE` filters would push filtering to the database and return only relevant rows.

## Proposed Fixes

1. **Server-side filtering:** Replace `_context.Reviews.ToListAsync()` + `.Where()` with `_context.Reviews.Where(r => r.ProductId == productId).ToListAsync()` at lines 54-55 and 70-71.
2. **Remove dead code in CreateReview:** Delete lines 95-97 entirely — the loaded reviews and computed average are assigned to a discard variable and never used.

## Expected Impact

- p95 latency: reduction of ~2-3ms on review-related requests.
- Overall p95 improvement: ~2-3% given reviews are ~20% of traffic.