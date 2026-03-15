# Reviews endpoints load all reviews then filter in memory

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

In `Controllers/ReviewsController.cs`, both read endpoints perform full table scans:

**GetReviewsByProduct (line 54):**
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

**GetAverageRating (line 70):**
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

Additionally, `CreateReview` (line 95-97) unnecessarily loads ALL reviews after saving just to compute an average that is never used:
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating); // Wasted computation
```

## Theory

The Reviews table likely contains thousands of seed records (the project uses `SeedData.cs`). Every call to `GetReviewsByProduct` or `GetAverageRating` materializes the entire Reviews table into tracked EF Core entities, then discards most of them via in-memory LINQ filtering. These two endpoints are called once each per VU iteration (by-product + average), accounting for ~11.1% of total request traffic.

The full table scans contribute to:
1. Unnecessary SQL Server load (the profiler shows sqlmin at ~69K samples)
2. Excessive memory allocations (contributing to the 218GB total and Gen2 GC pressure)
3. EF Core change tracking overhead for thousands of entities that are only read

The `GetAverageRating` endpoint is particularly wasteful: it loads all reviews into memory just to compute an average that SQL Server could compute directly with `AVG()`.

## Proposed Fixes

1. **Server-side filtering with AsNoTracking:** Replace full table loads with IQueryable filtering:
   - Line 54: `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync()`
   - Line 70-75: Use a database-side aggregate: `_context.Reviews.Where(r => r.ProductId == productId)` then compute count and average via `.CountAsync()` and `.AverageAsync(r => r.Rating)` (or a single GroupBy projection)

2. **Remove dead code in CreateReview:** Delete lines 95-97 — the post-save full table load and unused average computation.

## Expected Impact

- p95 latency: Expect 5-10% overall reduction. Moving filtering and aggregation to SQL Server eliminates unnecessary row transfer and entity materialization.
- Memory: Significant reduction in per-request allocations. Instead of materializing thousands of Review entities, only the matching rows (typically a few per product) are transferred.
- SQL Server CPU: Reduced data transfer and simpler query plans free up SQL Server resources for other concurrent requests.
