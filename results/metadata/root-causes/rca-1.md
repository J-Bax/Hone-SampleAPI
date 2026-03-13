# Reviews endpoints load entire table then filter in memory

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:54-55`, `GetReviewsByProduct` loads every review in the database into memory, then filters client-side:

```csharp
var allReviews = await _context.Reviews.ToListAsync();          // line 54
var filtered = allReviews.Where(r => r.ProductId == productId).ToList(); // line 55
```

The identical pattern appears in `GetAverageRating` at lines 70-71:

```csharp
var allReviews = await _context.Reviews.ToListAsync();          // line 70
var productReviews = allReviews.Where(r => r.ProductId == productId).ToList(); // line 71
```

The seed data creates ~2,000 reviews across 500 products (1–7 reviews each). Both endpoints load all ~2,000 reviews with full EF Core change tracking to return an average of ~4 rows per product.

The CPU profiling confirms this: `SingleQueryingEnumerable.MoveNextAsync` at 14.2% inclusive, `StateManager.StartTrackingFromQuery` and `NavigationFixer.InitialFixup` consuming massive CPU via SortedDictionary enumeration (17,152 exclusive samples). The GC report shows 49.8% of execution time in garbage collection with an inverted Gen2 >> Gen0 generation pattern — the `List<Review>` holding 2,000 entities (~400 KB+) likely exceeds the 85 KB LOH threshold, landing directly on the Large Object Heap and triggering frequent Gen2 collections.

These two endpoints are called every k6 iteration, contributing ~4,000 tracked entities per iteration out of ~13,000 total (31% of all entity materializations).

## Theory

By calling `.ToListAsync()` before `.Where()`, the LINQ filter executes in C# instead of being translated to a SQL WHERE clause. Every request materializes ~2,000 Review entities through EF Core's full pipeline: SQL reading (TdsParserStateObject), Unicode decoding, entity construction, identity map insertion (Dictionary.FindValue), change tracker registration (SortedDictionary), and navigation fixup. This happens twice per iteration (by-product + average), producing ~800 KB of tracked objects that are immediately discarded.

The resulting allocation pressure drives the catastrophic GC behavior: 82 Gen2 collections in 122 seconds, 935 MB/sec allocation rate, and a 2.1 GB peak heap. Since Gen2 collections are stop-the-world events, they stall ALL concurrent requests — not just the review endpoints — directly inflating the global p95 from what should be <100ms to 888ms.

## Proposed Fixes

1. **Server-side filtering for GetReviewsByProduct (lines 49-57):** Replace the full table load + in-memory filter with a server-side WHERE clause. Change lines 54-55 to `var filtered = await _context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync();`. Also remove the separate product existence check at line 50 — instead, return the filtered list (empty if product doesn't exist) or use a single `AnyAsync` on Products.

2. **Server-side aggregation for GetAverageRating (lines 64-83):** Replace lines 70-75 with server-side computation: use `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId)` and then `.CountAsync()` + `.AverageAsync(r => r.Rating)` (guarding for empty sets). This avoids materializing any Review entities at all — SQL Server computes the aggregate.

## Expected Impact

- p95 latency: ~15-20% overall reduction. Eliminating ~4,000 entity materializations per iteration (31% of total) will substantially reduce LOH allocations and Gen2 collection frequency, cutting GC pause time for all endpoints.
- Per-request latency for review endpoints: ~200-300ms reduction (from ~400ms to ~100-150ms) by avoiding 2,000 entity materializations per call.
- Allocation rate: ~25-30% reduction globally, easing GC from 49.8% toward ~30-35%.
