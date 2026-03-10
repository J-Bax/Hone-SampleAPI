# Eliminate full table scans in Home page

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28-29`, the home page loads **all 1,000 products** just to select 12 random featured items:

```csharp
var allProducts = await _context.Products.ToListAsync();
FeaturedProducts = allProducts.OrderBy(_ => Guid.NewGuid()).Take(12).ToList();
TotalProducts = allProducts.Count;
```

At `Pages/Index.cshtml.cs:36-37`, it loads **all ~2,000 reviews** to show 5 recent ones:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
RecentReviews = allReviews.OrderByDescending(r => r.CreatedAt).Take(5).ToList();
```

Neither query uses `AsNoTracking()`. The k6 scenario hits `GET /` every iteration (line 110 of baseline.js), making this one of the highest-traffic endpoints.

## Theory

Each home page request materializes ~3,000+ tracked entities (1,000 products + ~2,000 reviews + categories) only to discard 99.4% of them (keeps 12 + 5 = 17). Under 500 VUs, this pattern generates enormous allocation pressure:
- 1,000 Product entities × ~200 bytes each = ~200 KB per request just for products
- ~2,000 Review entities with Comment strings (up to 2,000 chars each) = ~4-8 MB per request for reviews
- All with change tracking overhead (identity maps, navigation fixup)

The `Comment` field (HasMaxLength 2000, see `AppDbContext.cs:41`) explains the CPU profiler's TDS character parsing and Unicode decoding hotspots — each review's comment string is read char-by-char from the TDS wire, decoded, and allocated as a managed string, only to be immediately discarded.

## Proposed Fixes

1. **Server-side featured products (lines 28-30):** Replace with `_context.Products.AsNoTracking().OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync()` for random selection, or simply `.AsNoTracking().Take(12).ToListAsync()` for deterministic selection. Get count separately: `TotalProducts = await _context.Products.CountAsync()`.

2. **Server-side recent reviews (lines 36-37):** Replace with `_context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync()`. This pushes ORDER BY + TOP to SQL.

3. **AsNoTracking on categories (line 33):** Add `.AsNoTracking()` to the categories query.

## Expected Impact

- **Allocation reduction:** ~3,000 tracked entities → ~27 untracked entities per request (~99% reduction)
- **p95 latency:** Estimated 10-15% improvement (fewer GC pauses, faster query execution)
- **RPS:** Estimated 10-15% improvement from reduced CPU in TDS parsing and entity tracking
- **Memory:** Peak heap should drop substantially as the home page was one of the largest per-request allocators
