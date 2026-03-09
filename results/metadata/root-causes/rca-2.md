# All review queries load entire Reviews table; CreateReview performs wasted computation

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

**`GetReviewsByProduct` (line 54):**
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```
Loads every review row into memory, then filters for one product.

**`GetAverageRating` (lines 70-71):**
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```
Same full table load to compute a single aggregate.

**`CreateReview` (lines 95-97):**
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating); // Wasted computation
```
After inserting a review, loads ALL reviews and computes an average that is assigned to a discard variable (`_`) and never used. This is pure waste on a write path.

The baseline scenario hits `by-product/{id}` and `average/{id}` on every iteration (`baseline.js` lines 65-66, 70-71), so under 500 VUs the entire Reviews table is scanned ~1,000 times per second.

## Theory

The Reviews table grows with every `CreateReview` call during the load test. As VUs create reviews, subsequent `GetReviewsByProduct` and `GetAverageRating` calls load an ever-growing table into memory. This creates a compounding performance degradation as the test progresses — later requests are slower than earlier ones because the table is larger. The `CreateReview` waste doubles the write-path cost by adding an unnecessary full table scan after every insert.

Without an index on `Review.ProductId`, even server-side WHERE clauses would require a table scan. The combination of client-side evaluation and no index makes this the second-largest bottleneck.

## Proposed Fixes

1. **Push filters and aggregates to SQL:** Replace `ToListAsync()` + client-side `Where` with `_context.Reviews.Where(r => r.ProductId == productId).ToListAsync()` in `GetReviewsByProduct` (line 54). In `GetAverageRating`, use `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` to compute the aggregate in SQL. Remove the dead computation in `CreateReview` (lines 95-97 entirely).

2. **Add a database index on `Review.ProductId`:** In `AppDbContext.OnModelCreating` (line 37-42), add `.HasIndex(e => e.ProductId)` so the server-side WHERE/AVG can use an index seek.

## Expected Impact

- **p95 latency:** ~15-20% reduction. Two review endpoints are hit per VU iteration; eliminating full table scans and the CreateReview waste should meaningfully reduce tail latency.
- **RPS:** ~10-15% increase from reduced SQL I/O and allocation pressure.
- **Error rate:** No change expected.
