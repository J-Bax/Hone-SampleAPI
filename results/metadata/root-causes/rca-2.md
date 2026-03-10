# Full-table Reviews loads for per-product filtering and aggregation

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54-55`, `GetReviewsByProduct` loads all ~2000 reviews into memory to filter for a single product:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

At lines 70-71, `GetAverageRating` does the same full-table load to compute an average for one product:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

Both endpoints are called every k6 VU iteration (baseline.js lines 65 and 70). Under 500 VUs, that's ~1000 full-table materializations of ~2000 reviews per second.

Additionally, `CreateReview` at lines 95-97 loads all reviews after saving, computes an average, and discards the result:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating);
```

## Theory

With ~2000 seeded reviews, each `ToListAsync()` materializes a `List<Review>` that likely exceeds 85KB (2000 reviews × ~150 bytes each ≈ 300KB), placing it directly on the Large Object Heap. The baseline scenario calls both `by-product` and `average` endpoints every iteration — so each VU iteration triggers 2 full-table loads of the Reviews table (plus a 3rd in CreateReview).

At peak load (500 VUs), this produces ~1500 LOH allocations per second from Reviews alone. Each allocation triggers Gen2 GC collection, contributing directly to the 12.9% GC pause ratio and 210ms max pause times. The SQL Server also wastes time reading and transferring ~2000 rows when only 1-7 are needed (each product has 1-7 reviews per SeedData).

## Proposed Fixes

1. **Server-side WHERE for GetReviewsByProduct:** Replace with `_context.Reviews.Where(r => r.ProductId == productId).ToListAsync()`. This returns only 1-7 rows instead of 2000, reducing the allocation from ~300KB to <2KB.

2. **Server-side aggregate for GetAverageRating:** Replace with `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` and `.CountAsync()`. This computes the aggregate in SQL, returning a single number instead of materializing 2000 objects.

3. **Remove wasted computation in CreateReview:** Delete lines 95-97 entirely — the result is assigned to a discard variable and never used.

## Expected Impact

- p95 latency: ~15-25% reduction. Each review endpoint goes from transferring ~300KB to <2KB per request, eliminating two major LOH allocation sources per VU iteration.
- RPS: ~15-20% increase from reduced SQL Server load (index scan vs full table scan) and dramatically reduced GC pressure.
- Gen2 collection count should drop substantially as 300KB review arrays no longer land on LOH.
