# Full Reviews table loaded into memory for every by-product and average query

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54`, `GetReviewsByProduct` loads all ~2,000 reviews to filter by one product:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

At `ReviewsController.cs:70-71`, `GetAverageRating` does the same:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

At `ReviewsController.cs:95-97`, `CreateReview` loads the entire table after insert for a completely unused computation:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating); // Wasted computation
```

The k6 scenario calls `by-product` and `average` endpoints every iteration (baseline.js lines 65-71), and `POST /api/reviews` is not called but `CreateReview` would compound the issue under write load. Each Review has a Comment field up to 2,000 chars (`AppDbContext.cs:41`), with actual comments ~70 chars each (`SeedData.cs:96-97`). The ~2,000 reviews with string fields match the CPU profile showing heavy TDS string parsing.

## Theory

Each `by-product` or `average` request materializes all ~2,000 Review entities with change tracking to return only 1-7 reviews for a single product. That's a 99.6% waste ratio. With two review endpoints called per VU iteration and up to 500 VUs, the server materializes ~2 million Review entities per second — all with full change tracking. The Review.Comment string field amplifies the Unicode decoding cost visible in the CPU profile (2.1% in `UnicodeEncoding.GetCharCount`). The `GetAverageRating` endpoint is especially wasteful: it materializes 2,000 entities to compute a single scalar that SQL Server could return directly via `AVG()`.

The wasted computation in `CreateReview` (line 97) loads the entire table on every write for a value that is assigned to a discard variable — pure waste.

## Proposed Fixes

1. **Server-side filtering with `.AsNoTracking()`:** In `GetReviewsByProduct` (line 54-55), replace with `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync()`. In `GetAverageRating` (line 70-75), push the aggregation to SQL: `await _context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => (double?)r.Rating) ?? 0.0` and `await _context.Reviews.CountAsync(r => r.ProductId == productId)` — this avoids materializing any entities at all.

2. **Remove wasted computation in `CreateReview`:** Delete lines 95-97 entirely. The loaded reviews and computed average are assigned to a discard and never used. This eliminates a full-table scan on every review creation.

## Expected Impact

- **p95 latency:** ~15-25% additional reduction (compounding with ProductsController fix). Two fewer full-table materializations per iteration.
- **RPS:** ~15-20% increase from reduced GC pressure and SQL round-trip time.
- **Allocation rate:** ~25-30% reduction for review-related endpoints. `GetAverageRating` goes from materializing ~2,000 entities to zero (scalar SQL query). `GetReviewsByProduct` materializes 1-7 entities instead of 2,000.
- **GC pressure:** Significant reduction in Gen2 frequency as review-related allocations drop by >99% per request.
