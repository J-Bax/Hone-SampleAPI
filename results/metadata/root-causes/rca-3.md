# Home page loads all products and all reviews for a handful of featured items

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Index.cshtml.cs:28-29`, the home page loads every product to select 12 featured:

```csharp
var allProducts = await _context.Products.ToListAsync();               // line 28
FeaturedProducts = allProducts.OrderBy(_ => Guid.NewGuid()).Take(12).ToList(); // line 29
```

At lines 36-37, it loads every review to show 5 recent:

```csharp
var allReviews = await _context.Reviews.ToListAsync();                 // line 36
RecentReviews = allReviews.OrderByDescending(r => r.CreatedAt).Take(5).ToList(); // line 37
```

Line 30 uses the full list for a count: `TotalProducts = allProducts.Count;`

This materializes ~3,000 entities (1,000 products + 2,000 reviews) with full change tracking to display 12 products, 5 reviews, and two counts.

## Theory

The home page is hit every k6 iteration (7.7% of traffic). Each request allocates ~3,000 tracked entities that are immediately reduced to 17 display items. The in-memory `OrderBy(_ => Guid.NewGuid())` forces materialization of a `Guid` per product plus a full sort — work that SQL Server could do far more efficiently (or be replaced with a simpler random sampling strategy).

At 52+ iterations/second, this page generates ~156,000 wasted entity allocations/second. Combined with the other full-table-scan sites, these contribute to the 935 MB/sec allocation rate and the LOH-dominated allocation pattern that causes 82 Gen2 collections in 122 seconds.

The `TotalProducts` count at line 30 could use `CountAsync()` instead of loading all entities just to count them.

## Proposed Fixes

1. **Server-side featured products (lines 28-30):** Replace with two efficient queries: `TotalProducts = await _context.Products.AsNoTracking().CountAsync();` for the count, and `FeaturedProducts = await _context.Products.AsNoTracking().OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync();` for featured items (or use a simpler server-side random strategy like `OrderBy(p => p.Id * someValue % somePrime).Take(12)`).

2. **Server-side recent reviews (lines 36-37):** Replace with `RecentReviews = await _context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync();`. This pushes the ORDER BY and TOP 5 to SQL Server, materializing only 5 entities instead of 2,000+.

## Expected Impact

- p95 latency: ~10-12% overall reduction. Eliminates ~2,973 wasted entity materializations per iteration (23% of total volume), further alleviating GC pressure.
- Per-request latency for the home page: ~250-300ms reduction.
- Combined with Opportunities 1 and 2, total entity materializations drop from ~13,000 to ~3,000 per iteration — a 77% reduction that should bring GC time from 49.8% down to under 15%, potentially cutting p95 from 888ms to under 300ms.
