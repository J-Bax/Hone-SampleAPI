# Root Cause Analysis — Experiment 12

> Generated: 2026-03-15 18:10:19 | Classification: narrow — The optimization consolidates redundant DB queries (e.g., separate product-existence check + review fetch in GetReviewsByProduct, and count + average in GetAverageRating) into fewer round trips, all within this single controller file's method bodies, with no changes to API contracts, dependencies, or other files.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 548.443835ms | 7546.103045ms |
| Requests/sec | 1006.2 | 125.5 |
| Error Rate | 0% | 0% |

---
# Consolidate redundant DB round trips in review endpoints

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:65-74`, `GetAverageRating` executes **three separate DB queries**:

```csharp
var product = await _context.Products.FindAsync(productId);        // Query 1: full entity with tracking
var reviewCount = await _context.Reviews.CountAsync(r => r.ProductId == productId);  // Query 2
var average = reviewCount > 0
    ? Math.Round(await _context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => (double)r.Rating), 2)  // Query 3
    : 0.0;
```

At `ReviewsController.cs:50-51`, `GetReviewsByProduct` uses `FindAsync` to load the full Product entity with change tracking just to check existence:

```csharp
var product = await _context.Products.FindAsync(productId);
if (product == null) return NotFound(...);
```

The CPU profile confirms SQL TDS parsing is the #1 cost center at 9.48% exclusive. Each unnecessary DB round trip adds ~1-2ms of LocalDB latency under contention.

## Theory

Every DB round trip incurs connection acquisition, command execution, and result materialization overhead. Under 500 VU stress load, the connection pool becomes a bottleneck — each extra round trip increases queuing time disproportionately. `GetAverageRating` makes 3 round trips where a single aggregate query would suffice. The `FindAsync` calls also load full Product entities with change tracking (allocating tracker entries) when only an existence check is needed. At ~11% of traffic (2 of 18 requests per VU iteration), these redundant round trips fire ~110 times/sec at peak load.

## Proposed Fixes

1. **Consolidate GetAverageRating into a single query:** Replace the 3 queries with a single `GroupBy` or conditional aggregate:
   ```csharp
   var stats = await _context.Reviews.Where(r => r.ProductId == productId)
       .GroupBy(r => r.ProductId)
       .Select(g => new { Count = g.Count(), Average = g.Average(r => (double)r.Rating) })
       .FirstOrDefaultAsync();
   ```
   Use `_context.Products.AsNoTracking().AnyAsync(p => p.Id == productId)` for the existence check instead of `FindAsync`.

2. **Replace FindAsync with AnyAsync in GetReviewsByProduct (line 50):** Use `AsNoTracking().AnyAsync(p => p.Id == productId)` — avoids materializing the full entity and eliminates change tracker allocation.

## Expected Impact

- p95 latency: ~4-8ms reduction on affected requests (eliminating 2-3 DB round trips)
- RPS: slight improvement from reduced connection pool contention
- Allocation reduction from eliminating change tracker entries for Product entities
- Overall p95 improvement: ~0.8-1.5%, roughly 4-8ms off the 548ms p95

