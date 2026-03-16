# Root Cause Analysis — Experiment 21

> Generated: 2026-03-15 22:42:06 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 544.804015ms | 7546.103045ms |
| Requests/sec | 1078.1 | 125.5 |
| Error Rate | 0% | 0% |

---
# Eliminate redundant category existence DB round trip in GetProductsByCategory

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:49-61`, the `GetProductsByCategory` endpoint makes two separate database round trips:

```csharp
var categoryExists = await _context.Categories
    .AsNoTracking()
    .AnyAsync(c => c.Name == categoryName);       // Round trip 1

if (!categoryExists)
    return NotFound(...);

var filtered = await _context.Products
    .AsNoTracking()
    .Where(p => p.Category == categoryName)
    .ToListAsync();                                // Round trip 2
```

By contrast, `ReviewsController.GetReviewsByProduct` (lines 49-62) already uses the optimized pattern: query first, only check existence on empty results.

## Theory

Every VU iteration hits `GET /api/products/by-category/Electronics`, which always has results (Electronics is a seeded category). The first `AnyAsync` query against the Categories table is redundant in the common case — it adds a full DB round trip (network latency + query execution) that yields no value when products exist for that category. Under 500 concurrent VUs, these redundant round trips compete for SQL Server connection pool slots and add contention. The CPU profiler shows SqlDataReader.PrepareAsyncInvocation at 0.26% — per-query async overhead that scales linearly with query count.

## Proposed Fixes

1. **Query products first, conditional existence check:** Query products by category first. If results are non-empty, return them immediately (category existence is implicitly proven). Only if results are empty, run the `AnyAsync` on Categories to distinguish 404 from empty list — exactly mirroring the pattern in `ReviewsController.GetReviewsByProduct` (lines 49-62).

## Expected Impact

- p95 latency: ~3-5ms reduction per affected request (eliminates one DB round trip)
- RPS: slight improvement from reduced connection pool contention
- This endpoint is ~5.5% of total traffic. At ~4ms savings, overall p95 improvement is ~0.4%.

