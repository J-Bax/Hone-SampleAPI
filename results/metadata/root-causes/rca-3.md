# Server-side pagination and filtering in Products page

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:35`, the products listing page loads **all 1,000 products** into memory before applying filtering and pagination client-side:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

Filtering is done in memory (lines 38-51):

```csharp
if (!string.IsNullOrWhiteSpace(category))
{
    allProducts = allProducts.Where(p =>
        p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
}
```

Pagination is also client-side (lines 57-60) despite `PageSize = 24`:

```csharp
Products = allProducts
    .Skip((CurrentPage - 1) * PageSize)
    .Take(PageSize)
    .ToList();
```

No `AsNoTracking()` is used. The k6 scenario hits `GET /Products` every iteration (line 115 of baseline.js).

## Theory

The page has built-in pagination (PageSize=24) but defeats its purpose by loading all 1,000 products first. Each request materializes 1,000 tracked Product entities to display 24. The in-memory `StringComparison.OrdinalIgnoreCase` filtering prevents SQL Server from using any indexes on the Category column. Combined with the categories sidebar query (line 63, also without AsNoTracking), each request creates ~1,010 tracked entities.

At 500 VUs, this adds ~500K tracked Product entities per second to the allocation pressure, contributing to the 189 GB total allocations and 100 Gen2 collections observed in the profiling run.

## Proposed Fixes

1. **Build an IQueryable pipeline with server-side filtering and pagination (lines 35-60):** Start with `IQueryable<Product> query = _context.Products.AsNoTracking()`, apply `.Where()` filters conditionally, get count via `await query.CountAsync()`, then apply `.Skip().Take().ToListAsync()`. This pushes filtering and pagination to SQL, returning only 24 rows.

2. **AsNoTracking on categories query (line 63):** Add `.AsNoTracking()` to the categories sidebar query.

## Expected Impact

- **Allocation reduction:** 1,000 tracked entities → 24 untracked entities per request (~97.6% reduction)
- **p95 latency:** Estimated 5-10% improvement (smaller contribution than Detail/Home pages since Products have smaller payloads than Reviews)
- **RPS:** Estimated 5-8% improvement from reduced SQL data transfer and entity materialization
- **Combined impact of all three Razor page fixes:** p95 latency improvement of 25-40% (from ~566ms toward ~340-420ms), RPS improvement of 25-35% (from ~984 toward ~1,250-1,330)
