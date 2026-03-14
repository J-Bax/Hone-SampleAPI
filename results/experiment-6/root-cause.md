# Root Cause Analysis — Experiment 6

> Generated: 2026-03-14 14:06:12 | Classification: narrow — Optimization moves filtering and pagination to database queries instead of loading all products into memory, modifying only this file's OnGetAsync method with existing DbContext calls.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 482.42257ms | 2054.749925ms |
| Requests/sec | 1321.2 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Products page loads entire Products table for client-side pagination

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:35`, the `OnGetAsync` method loads every product into memory before filtering or paginating:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

With 1,000 seeded products (each carrying Name, Description, Category, Price — see `Data/SeedData.cs:37-49`), this materializes ~1,000 entities per request. The k6 scenario calls `GET /Products` with no query parameters (line `const productsPageRes = http.get(${BASE_URL}/Products)`), so both the category filter (line 38) and search filter (line 46) are skipped, and the in-memory pagination at lines 57-60 simply takes the first 24 products after loading all 1,000:

```csharp
Products = allProducts
    .Skip((CurrentPage - 1) * PageSize)
    .Take(PageSize)
    .ToList();
```

The CPU profiler confirms this pattern: `StateManager.StartTrackingFromQuery` (1.8% inclusive), `TdsParserStateObject.TryReadChar` (2.9% exclusive), and `UnicodeEncoding.GetChars` (1.2%) all point to materializing large result sets. The memory profiler shows 524 MB/sec allocation rate and 32.7% GC pause ratio — bulk entity materialization is a major contributor.

## Theory

Every VU iteration loads all 1,000 Product entities from SQL Server, materializes them into tracked EF Core objects (allocating InternalEntityEntry, identity map entries, navigation fixup), reads their string-heavy columns (Name ~30 chars, Description ~100 chars, Category ~15 chars) over the TDS wire, then immediately discards 976 of them. Under 500 concurrent VUs in the stress phase, this generates massive GC pressure: ~318KB of unnecessary allocations per request × hundreds of concurrent requests = a significant fraction of the 524 MB/sec allocation rate. The GC pause ratio of 32.7% is the primary latency driver at this stage (max pause 1,772ms directly explains the error rate), and reducing allocation volume is the most effective way to reduce it.

## Proposed Fixes

1. **Server-side filtering and pagination:** Build an `IQueryable<Product>` with conditional `.Where()` clauses for category and search, then apply `.Skip()` and `.Take()` before calling `.ToListAsync()`. Use a separate `.CountAsync()` on the filtered queryable for `TotalPages`. Also add `.AsNoTracking()` since this is a read-only page. This keeps all filtering and pagination in SQL Server, returning only the 24 needed products.

## Expected Impact

- p95 latency: ~20-40ms reduction per request (97.6% less entity materialization, DB I/O, and string decoding)
- GC pressure: ~318KB fewer allocations per request, reducing overall allocation rate by ~5-6% and proportionally reducing GC pause frequency
- RPS: modest improvement from reduced per-request CPU time
- The p95 has plateaued at ~480ms across experiments 3-5, suggesting GC pauses are the dominant remaining bottleneck. Reducing a major allocation source should help break through this plateau.

