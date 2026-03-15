# Replace dual full-table scans with targeted queries on Home page

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28-29`, the home page loads **every product** into memory with change tracking just to select 12 random featured items:

```csharp
var allProducts = await _context.Products.ToListAsync();
FeaturedProducts = allProducts.OrderBy(_ => Guid.NewGuid()).Take(12).ToList();
TotalProducts = allProducts.Count;
```

At lines 36-37, it does the same with **all reviews** just to get the 5 most recent:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
RecentReviews = allReviews.OrderByDescending(r => r.CreatedAt).Take(5).ToList();
```

Neither query uses `AsNoTracking()`. The CPU profile shows EF Core change tracking as a significant overhead: NavigationFixer (756 samples), InternalEntityEntry constructor (628 samples), SortedDictionary enumeration (~5300 samples). The GC report shows 95.4GB total allocations over ~120s — materializing two large tables with full tracking on every home page request is a major contributor.

## Theory

With potentially thousands of products and reviews seeded in the database, every home page request creates two full table scans, materializes all rows into tracked entity objects, sorts them in memory, then discards all but 12/5 items. This is wasteful in three ways:

1. **SQL data transfer**: All columns of all rows are transmitted over TDS, driving the Unicode decoding hotspot (8900 samples in UnicodeEncoding).
2. **Change tracking overhead**: Every materialized entity gets an InternalEntityEntry, NavigationFixer fixup, and identity map registration — completely unnecessary for read-only display.
3. **Memory allocation**: Large List<T> allocations for thousands of entities inflate the managed heap, contributing to the 3.4GB peak and Gen2-dominant GC pattern.

## Proposed Fixes

1. **Server-side random selection + Take for products**: Replace the full scan with `_context.Products.AsNoTracking().OrderBy(_ => EF.Functions.Random()).Take(12).ToListAsync()` or, if EF.Functions.Random() is unavailable, use `_context.Products.AsNoTracking().OrderBy(p => p.Id).Take(12).ToListAsync()` for deterministic featured products. Get TotalProducts via `_context.Products.CountAsync()` instead of loading all.

2. **Server-side ordering + Take for reviews**: Replace `_context.Reviews.ToListAsync()` at line 36 with `_context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync()`.

## Expected Impact

- p95 latency: ~80-120ms reduction per home page request by eliminating two full scans
- GC pressure: Significant reduction — materializing 17 entities instead of thousands eliminates LOH allocations
- SQL Server CPU: Reduced TDS parsing and data transfer overhead
- Overall p95 improvement: ~1.5-2.5%, with indirect benefits to all endpoints from reduced SQL/GC contention
