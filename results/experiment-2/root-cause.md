# Root Cause Analysis — Experiment 2

> Generated: 2026-03-15 12:02:30 | Classification: narrow — The optimization replaces client-side `.ToListAsync()` + `.Where()` with server-side `.Where().ToListAsync()` in method bodies within a single controller file, changing only query internals without altering routes, response shapes, dependencies, or other files.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 2203.84149ms | 7546.103045ms |
| Requests/sec | 341.5 | 125.5 |
| Error Rate | 0% | 0% |

---
# Replace full reviews table scan with server-side query filtering

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

Both read-by-product endpoints in `ReviewsController.cs` load the entire `Reviews` table (~2,000 rows from seed data, growing under load) into memory:

- **Line 54** (`GetReviewsByProduct`):
  ```csharp
  var allReviews = await _context.Reviews.ToListAsync();
  var filtered = allReviews.Where(r => r.ProductId == productId).ToList();
  ```
- **Line 70** (`GetAverageRating`):
  ```csharp
  var allReviews = await _context.Reviews.ToListAsync();
  var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();
  var average = productReviews.Any()
      ? Math.Round(productReviews.Average(r => r.Rating), 2) : 0.0;
  ```
- **Lines 95-97** (`CreateReview`): After saving, pointlessly loads ALL reviews again and computes an unused average:
  ```csharp
  var allReviews = await _context.Reviews.ToListAsync();
  var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
  var _ = productReviews.Average(r => r.Rating); // Wasted computation
  ```

The Reviews table starts with ~2,000 rows and each product has 1-7 reviews, meaning each request materializes ~2,000 objects to return ~4. The CPU profile shows the top hotspot chain flowing through EF materialization and TDS parsing, and the GC report confirms 37.3 GB total allocations with catastrophic Gen2 pressure.

## Theory

Each call to `GetReviewsByProduct` and `GetAverageRating` materializes ~2,000 Review entities with full change tracking, only to discard ~99.8% of them. With these 2 endpoints representing 11.1% of k6 traffic, this pattern generates ~10% of total allocation volume. The `GetAverageRating` endpoint is particularly wasteful because SQL Server can compute `AVG(Rating)` directly — materializing 2,000 rows to compute an average in C# turns a trivial SQL aggregate into a multi-megabyte allocation.

The `CreateReview` endpoint (line 95-97) adds insult to injury: it loads all reviews *again* after saving, computes an average, and discards it — pure waste. While `CreateReview` isn't called in the k6 scenario, it demonstrates the pervasive anti-pattern.

## Proposed Fixes

1. **Server-side WHERE + AsNoTracking:** Replace `ToListAsync()` + LINQ-to-Objects with:
   - `GetReviewsByProduct` (line 54): `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync()`
   - `GetAverageRating` (lines 70-75): Use database aggregation: `_context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => (double?)r.Rating) ?? 0.0` and `_context.Reviews.CountAsync(r => r.ProductId == productId)` — zero entity materialization.
   - `CreateReview` (lines 95-97): Delete the dead code entirely.

## Expected Impact

- **p95 latency:** Estimated 7-9% overall improvement. Per-request allocations drop from ~2,000 entities to ~4 (for by-product) or zero (for average, using SQL aggregation).
- **RPS:** Moderate increase from reduced GC pressure.
- **Allocation volume:** Estimated 8-10% reduction in total allocations, directly reducing Gen2 collection frequency.

