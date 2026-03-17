# Root Cause Analysis — Experiment 36

> Generated: 2026-03-16 20:14:39 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 7546.103045ms | 7546.103045ms |
| Requests/sec | 125.5 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add result limit to search endpoint returning all 1000 products

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:97-110`, the search endpoint with a query matches all products:

```csharp
var results = await _context.Products
    .AsNoTracking()
    .Where(p => EF.Functions.Like(p.Name, $"%{q}%") ||
                EF.Functions.Like(p.Description, $"%{q}%"))
    .Select(p => new Product
    {
        Id = p.Id, Name = p.Name, Price = p.Price,
        Category = p.Category, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt
    })
    .ToListAsync();
```

The k6 scenario calls `GET /api/products/search?q=Product` every iteration. Since all 1000 products are named `Product XXXX - Category`, the `LIKE '%Product%'` predicate matches **every row**. The full-table scan returns all 1000 rows, which are then serialized to ~100-150KB of JSON.

The fallback path at lines 114-126 (when `q` is empty) also returns all 1000 products without any limit.

## Theory

The `LIKE '%Product%'` pattern cannot use any index (leading wildcard forces a full scan). SQL Server reads all 1000 rows, evaluates the predicate on each, and returns the entire result set. The application then serializes 1000 projected Product objects into JSON. Under 500 concurrent VUs, this creates:
- Heavy DB I/O reading the full Products table on every search request
- Significant CPU pressure from serializing large JSON arrays
- GC pressure from allocating 1000-element List<Product> per request
- Large response payloads consuming buffer pool memory

With `TOP 50` (via `.Take(50)`), SQL Server can stop scanning after finding 50 matching rows — which happens almost immediately since all rows match. This reduces rows scanned by ~95%, serialization work by 95%, and payload size by 95%.

## Proposed Fixes

1. **Add `.Take(50)` to both search paths:** Insert `.Take(50)` before `.ToListAsync()` on both the filtered search (line 110) and the fallback all-products path (line 125). This caps results at 50 without changing the API contract shape (still returns an array of products). Search APIs conventionally return bounded results.

## Expected Impact

- p95 latency: estimated ~25-35ms reduction per search request (DB scan reduction + 95% less serialization)
- RPS: should increase due to freed CPU and DB capacity
- Overall p95 improvement: ~3% (5.6% traffic share × ~30ms / ~544ms current p95)

