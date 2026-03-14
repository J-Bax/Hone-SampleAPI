# Root Cause Analysis — Experiment 3

> Generated: 2026-03-13 19:05:53 | Classification: narrow — Fix loads entire Reviews and Products tables into memory then filters—optimization to use LINQ `.Where()` and `.Include()` for server-side filtering fits entirely within this single file's OnGetAsync method.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 844.12ms | 888.549155000001ms |
| Requests/sec | 717.3 | 683.2 |
| Error Rate | 0% | 0% |

---
# Product detail page loads entire Reviews and Products tables

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:34-36`, the detail page loads every review in the database into memory, then filters client-side:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
Reviews = allReviews.Where(r => r.ProductId == id)
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList();
```

At `Pages/Products/Detail.cshtml.cs:41-46`, it loads every product into memory just to find 4 related products:

```csharp
var allProducts = await _context.Products.ToListAsync();
RelatedProducts = allProducts
    .Where(p => p.Category == Product.Category && p.Id != id)
    .OrderBy(_ => Guid.NewGuid())
    .Take(4)
    .ToList();
```

With seed data of ~2,000 reviews and 1,000 products, every detail page request transfers ~3,000 rows from SQL Server into the app process, only to discard the vast majority.

## Theory

Each `/Products/Detail/{id}` request triggers two full-table scans: one on Reviews (~2,000 rows) and one on Products (1,000 rows). Under high concurrency (300-500 VUs), this creates massive memory allocation pressure and database I/O contention. The GC must collect thousands of short-lived objects per request, and SQL Server must scan and transmit entire tables repeatedly. Since this endpoint is hit once per VU iteration (~7.7% of all traffic), the aggregate effect on p95 latency is significant.

## Proposed Fixes

1. **Server-side review filtering:** Replace `_context.Reviews.ToListAsync()` with `_context.Reviews.Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToListAsync()` at line 34-37. This pushes the filter to SQL Server.

2. **Server-side related products query:** Replace `_context.Products.ToListAsync()` with `_context.Products.Where(p => p.Category == Product.Category && p.Id != id).Take(4).ToListAsync()` at lines 41-46. The random ordering can be dropped (or use a SQL-compatible approach) since the goal is just 4 related products.

## Expected Impact

- p95 latency reduction per request: ~60-80ms (eliminating transfer and materialization of ~3,000 unnecessary rows)
- Overall p95 improvement: ~5-7% (7.7% traffic share × ~70ms reduction on 844ms baseline)
- Memory allocation reduction: significant decrease in Gen0 GC pressure from short-lived entity objects

