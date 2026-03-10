# Root Cause Analysis — Experiment 2

> Generated: 2026-03-09 23:04:53 | Classification: narrow — Optimization modifies only method bodies in this single controller file to add WHERE filtering and LINQ aggregation at the query level, does not change endpoints/schemas/dependencies, and requires no test modifications.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 644.64646ms | 888.549155000001ms |
| Requests/sec | 859 | 683.2 |
| Error Rate | 0% | 0% |

---
# Eliminate full Reviews table scan with server-side filtering and SQL aggregation

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

Both `GetReviewsByProduct` and `GetAverageRating` are called once per VU iteration, and both load the entire Reviews table (~2000 rows) into memory before filtering:

`ReviewsController.cs:54-55`:
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
```

`ReviewsController.cs:70-71`:
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
```

Additionally, `CreateReview` (line 95-97) reloads the entire Reviews table just to compute an average that's never used:
```csharp
var allReviews = await _context.Reviews.ToListAsync();
var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
var _ = productReviews.Average(r => r.Rating); // Wasted computation
```

## Theory

Each review has `Comment` (up to 2000 chars nvarchar), `CustomerName` (100 chars), plus other fields. Loading ~2000 reviews means ~2000 tracked entities × ~1KB+ = ~2MB+ per request through the EF materialization pipeline, with all the TDS string decoding and change tracking overhead visible in the CPU profile (`TryReadChar` at 1.6%, `UnicodeEncoding` at 2.1%). With 2 calls per VU iteration at 500 VUs, this is the second largest source of allocations.

The product typically has only 1-7 reviews, so 99.5%+ of materialized entities are discarded — pure waste. The `GetAverageRating` endpoint materializes ~2000 entities just to compute a single scalar that SQL Server could compute with `SELECT AVG(Rating)`. The wasted computation in `CreateReview` adds unnecessary load on write operations.

## Proposed Fixes

1. **Server-side filtering for `GetReviewsByProduct`** (line 54-55): Replace with `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync()`. This pushes the `WHERE ProductId = @p` to SQL, returning only 1-7 rows instead of 2000.

2. **SQL aggregation for `GetAverageRating`** (line 70-74): Replace the full table load with `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating)` and `.CountAsync()`. This computes the aggregate in SQL without materializing any entities.

3. **Remove wasted computation in `CreateReview`** (lines 95-97): Delete the three lines that reload all reviews and compute an unused average.

## Expected Impact

- **p95 latency**: ~15-20% reduction. Two fewer full-table materializations per VU iteration.
- **RPS**: ~15-25% increase from reduced CPU and memory pressure.
- **GC**: Substantial reduction — eliminates ~4000 tracked entity materializations per VU iteration (2 calls × ~2000 reviews), directly reducing Gen2 GC pressure.

