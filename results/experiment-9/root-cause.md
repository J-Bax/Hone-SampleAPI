# Root Cause Analysis — Experiment 9

> Generated: 2026-03-10 02:30:34 | Classification: narrow — Eliminating redundant product-existence checks (lines 50 and 67) can be achieved by removing tracked `.FindAsync()` calls and replacing with a single `.Any()` query, contained entirely within ReviewsController.cs methods.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 408.6216ms | 888.549155000001ms |
| Requests/sec | 1345.6 | 683.2 |
| Error Rate | 0% | 0% |

---
# Eliminate redundant tracked product-existence queries

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

Both `GetReviewsByProduct` and `GetAverageRating` perform a **tracked `FindAsync`** solely to check if a product exists:

`ReviewsController.cs:50-52`:
```csharp
var product = await _context.Products.FindAsync(productId);
if (product == null)
    return NotFound(new { message = $"Product with ID {productId} not found" });
```

`ReviewsController.cs:67-69`:
```csharp
var product = await _context.Products.FindAsync(productId);
if (product == null)
    return NotFound(new { message = $"Product with ID {productId} not found" });
```

These calls:
1. Execute `SELECT TOP 1 Id, Name, Description, Price, Category, CreatedAt, UpdatedAt FROM Products WHERE Id = @p0` — fetching **all 7 columns** when only existence matters
2. Materialize a full `Product` entity with **change tracking** (identity map entry, snapshot copy)
3. Hold the tracked entity in the DbContext for the request lifetime

The load test calls both endpoints every iteration (`/api/reviews/by-product/{id}` at line 65 and `/api/reviews/average/{id}` at line 70 of `baseline.js`), generating ~200+ unnecessary tracked Product loads/sec.

Additionally, `GetAverageRating` at lines 71-73 executes **three separate queries** (FindAsync + CountAsync + conditional AverageAsync) when a single query could compute both count and average:

```csharp
var reviewCount = await _context.Reviews.CountAsync(r => r.ProductId == productId);
var average = reviewCount > 0
    ? Math.Round(await _context.Reviews.Where(r => r.ProductId == productId).AverageAsync(r => r.Rating), 2)
    : 0.0;
```

## Theory

`FindAsync` without `AsNoTracking` creates a tracked entity: EF Core allocates a snapshot copy for change detection and registers the entity in the identity map. The CPU profile shows 1.8% in `CastHelpers` (entity materialization casting) and 0.27% in DI/dictionary resolution — both amplified by tracked entities. With ~200 calls/sec, this adds ~200 unnecessary allocations/sec of Product entities + their snapshots, contributing to the 627 MB/sec allocation rate.

The triple-query pattern in `GetAverageRating` also adds an extra SQL round-trip: FindAsync (1 RT), CountAsync (1 RT), AverageAsync (1 RT) = 3 round-trips when 1-2 would suffice. Each round-trip adds network + SQL Server scheduling latency, directly inflating p95.

## Proposed Fixes

1. **Replace `FindAsync` with `AnyAsync`** at lines 50-52 and 67-69:
   ```csharp
   var productExists = await _context.Products.AnyAsync(p => p.Id == productId);
   ```
   This generates `SELECT CASE WHEN EXISTS(SELECT 1 FROM Products WHERE Id = @p0) THEN 1 ELSE 0 END` — no entity materialization, no tracking, minimal data transfer.

2. **Consolidate GetAverageRating into fewer queries** at lines 71-73: use a single `GroupBy` or fetch reviews once and compute both count and average in memory, eliminating one SQL round-trip.

## Expected Impact

- **p95 latency**: −10 to 20ms (2–5% reduction). Eliminates ~1 SQL round-trip from GetAverageRating and reduces materialization overhead from both endpoints.
- **Allocation rate**: −5–10 MB/sec from eliminating ~200 tracked Product entities/sec plus their change-tracking snapshots.
- **RPS**: +2–3%. Fewer queries per request frees SQL Server and connection pool capacity.

