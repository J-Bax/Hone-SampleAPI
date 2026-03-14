# Home page loads all products and reviews for sampling

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28-29`, the home page loads the entire Products table just to randomly pick 12:

```csharp
var allProducts = await _context.Products.ToListAsync();
FeaturedProducts = allProducts.OrderBy(_ => Guid.NewGuid()).Take(12).ToList();
```

At `Pages/Index.cshtml.cs:36-37`, it loads the entire Reviews table to get 5 recent reviews:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
RecentReviews = allReviews.OrderByDescending(r => r.CreatedAt).Take(5).ToList();
```

This transfers 1,000 products + ~2,000 reviews = ~3,000 rows per home page request.

## Theory

The home page (`/`) is hit once per VU iteration. Under load, materializing 3,000 entities per request causes excessive database I/O, network transfer, and GC pressure. The `OrderBy(Guid.NewGuid())` for random sampling forces a full in-memory sort of 1,000 objects. The reviews sort does the same across ~2,000 objects. All of this work is wasted since only 17 rows (12 products + 5 reviews) are actually needed.

## Proposed Fixes

1. **Server-side recent reviews:** Replace the all-reviews load with `_context.Reviews.OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync()` at lines 36-37. This pushes sorting and limiting to SQL Server.

2. **Server-side featured products sampling:** Replace the all-products load at line 28 with a query that limits results at the database level. Use `_context.Products.OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync()` (SQL Server compatible via `NEWID()`) or simply `.Take(12)` for deterministic featured products. Get `TotalProducts` via a separate `CountAsync()` call.

## Expected Impact

- p95 latency reduction per request: ~50-70ms (eliminating ~3,000 row materialization + in-memory sorts)
- Overall p95 improvement: ~4-6% (7.7% traffic share × ~60ms saving)
- Additional benefit: reduced Gen0 GC collections under high concurrency
