# Full table scan of ~2000 Reviews with in-memory filtering

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

Both review read endpoints exercised by k6 load the entire ~2,000-row Reviews table:

**GetReviewsByProduct** (`ReviewsController.cs:54-55`):
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```
Loads all ~2,000 reviews with full change tracking, then filters in memory for a single product's reviews (typically 1-7 rows).

**GetAverageRating** (`ReviewsController.cs:70-75`):
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
var average = productReviews.Any()
    ? Math.Round(productReviews.Average(r => r.Rating), 2)
    : 0.0;
```
Loads all ~2,000 reviews just to compute a single average — an operation SQL Server can do natively with `AVG()`. Each Review includes a Comment string (~80 chars), so materializing 2,000 reviews transfers ~160KB of comment text per request that is immediately discarded.

Additionally, both endpoints first verify the product exists with a separate `FindAsync` (lines 50, 66), adding an extra DB round trip before the full table scan.

## Theory

Under the k6 scenario, the `by-product` and `average` endpoints together account for ~11.1% of all traffic (2 of 18 requests per VU iteration). Each request:

1. **Transfers ~2,000 rows** including Comment strings via TDS protocol — the `UnicodeEncoding.GetChars` hotspot (1.5% exclusive CPU) is partly driven by decoding these string columns.
2. **Materializes 2,000 Review entities** with full change tracking — each entity goes through `StartTrackingFromQuery` → `IdentityMap.Add` → `NavigationFixer.InitialFixup`, contributing to the 4.8% inclusive CPU in NavigationFixer.
3. **Allocates ~200KB+ per request** for the List<Review> backing array plus 2,000 Review objects. Under 500 VUs, this adds ~100MB/sec to the allocation rate for these two endpoints alone.
4. **GetAverageRating is especially wasteful**: SQL Server's `AVG()` function could return a single scalar value in one row, but instead the app transfers 2,000 rows and computes the average in C#.

The `seededId(500, 2)` in k6 means `reviewProductId` ranges 1-500, matching seeded data — so every review request hits a valid product with reviews, maximizing the per-request cost.

## Proposed Fixes

1. **Server-side WHERE + AsNoTracking()**: Replace `_context.Reviews.ToListAsync()` followed by in-memory `Where()` with `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync()` in GetReviewsByProduct. This reduces result sets from ~2,000 to 1-7 rows.

2. **Push AVG to SQL**: In GetAverageRating, replace the full table scan + in-memory Average with server-side aggregation: use `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` and `CountAsync()`. This returns a single scalar instead of 2,000 rows.

## Expected Impact

- **p95 latency**: Estimated ~200ms reduction per request. GetReviewsByProduct drops from ~2,000 to ~4 materialized entities. GetAverageRating drops from ~2,000 materialized entities to a single SQL aggregate returning 1 row.
- **GC pressure**: Eliminating ~2,000 Review entity allocations per request across 11.1% of traffic reduces allocation rate by an estimated ~100MB/sec, helping reduce the 14.3% GC pause ratio.
- **Overall p95 improvement**: ~1.4% (11.1% traffic × 200ms / 1596ms). Reduced allocation pressure provides secondary benefits to all endpoints.
