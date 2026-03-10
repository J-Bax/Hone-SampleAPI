# Combine GetAverageRating into single aggregation query

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:67–74` (GetAverageRating):

```csharp
var product = await _context.Products.FindAsync(productId);       // Query 1: tracked
if (product == null)
    return NotFound(...);

var reviewCount = await _context.Reviews
    .CountAsync(r => r.ProductId == productId);                    // Query 2
var average = reviewCount > 0
    ? Math.Round(await _context.Reviews
        .Where(r => r.ProductId == productId)
        .AverageAsync(r => r.Rating), 2)                           // Query 3
    : 0.0;
```

This endpoint makes **3 separate DB round trips**:
1. `FindAsync(productId)` — tracked query, materializes full Product entity with change tracking overhead
2. `CountAsync(r => r.ProductId == productId)` — aggregate scan on Reviews index
3. `AverageAsync(r => r.Rating)` — second aggregate scan on the **same** Reviews index range

The endpoint is called every VU iteration (`baseline.js:70`: `http.get(BASE_URL + '/api/reviews/average/' + reviewProductId)`). With `seededId(500, 2)`, it covers a wide range of product IDs, each with 1–7 reviews (`SeedData.cs:88`).

The Reviews.ProductId index exists (`AppDbContext.cs:43`: `entity.HasIndex(e => e.ProductId)`), so individual queries are fast — but the 3-round-trip overhead is the issue.

## Theory

Each DB round trip incurs: connection pool checkout, command preparation, network transit (even over LocalDB shared memory ~0.1ms), result parsing, and connection return. At 1,345 RPS total throughput with 13 endpoints per VU iteration, this endpoint handles ~100+ req/s. Three round trips per call means ~300+ DB operations/sec just for this endpoint.

The `FindAsync` also adds change-tracking overhead: the full Product entity (Name, Description, Price, Category, dates) is loaded and tracked in the DbContext, consuming memory that becomes GC pressure. The Product is never modified — purely a wasted tracked allocation.

Queries 2 and 3 scan the same index range (`WHERE ProductId = @p`) twice — SQL Server reads the same data pages from its buffer pool twice per request.

## Proposed Fixes

1. **Single GroupBy aggregation:** Replace the Count + Average with a single query:
   ```csharp
   var stats = await _context.Reviews
       .Where(r => r.ProductId == productId)
       .GroupBy(r => r.ProductId)
       .Select(g => new { Count = g.Count(), Average = g.Average(r => r.Rating) })
       .FirstOrDefaultAsync();
   ```
   This scans the ProductId index once and computes both aggregates in a single SQL pass. If `stats` is null, no reviews exist for that product ID.

2. **Replace FindAsync with AnyAsync for existence check:** Change `FindAsync(productId)` to `_context.Products.AsNoTracking().AnyAsync(p => p.Id == productId)` — returns a bool without materializing the full entity. Combined with fix #1, this reduces the endpoint from 3 queries to 2 (existence + aggregation), or even 1 if you check product existence only when the aggregation returns null.

## Expected Impact

- p95 latency: ~3–5% reduction from eliminating 1–2 DB round trips per call on a hot endpoint
- Connection pool pressure: ~30% fewer checkouts for this endpoint path
- Memory/GC: small reduction from not tracking Product entities (~100+ tracked allocations/sec eliminated)
- The Count+Average combination alone halves the review-index scan I/O for this endpoint
