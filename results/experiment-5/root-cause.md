# Root Cause Analysis — Experiment 5

> Generated: 2026-03-10 00:40:52 | Classification: narrow — Eliminating full table scans requires only optimizing the DbContext queries within this single page model file (e.g., adding `.Where()` filters before `.ToListAsync()` and replacing inefficient LINQ-to-Objects with LINQ-to-SQL), with no changes to dependencies, migrations, API routes, response schemas, or test files.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 566.445435ms | 888.549155000001ms |
| Requests/sec | 983.6 | 683.2 |
| Error Rate | 0% | 0% |

---
# Eliminate full table scans in Product Detail page

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:34-37`, the product detail page loads **every review in the database** into memory, then filters client-side:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
Reviews = allReviews.Where(r => r.ProductId == id)
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList();
```

At `Pages/Products/Detail.cshtml.cs:41-46`, it also loads **every product** to find 4 related items:

```csharp
var allProducts = await _context.Products.ToListAsync();
RelatedProducts = allProducts
    .Where(p => p.Category == Product.Category && p.Id != id)
    .OrderBy(_ => Guid.NewGuid())
    .Take(4)
    .ToList();
```

Neither query uses `AsNoTracking()`. The k6 baseline scenario calls `GET /Products/Detail/{id}` every iteration (line 120 of baseline.js), so under 500 VUs this endpoint is hit hundreds of times per second.

The CPU profiler confirms: TDS character parsing (1.78%), Unicode decoding (1.76%), and EF change tracking (0.39% + 0.84%) are top hotspots — all driven by materializing thousands of tracked entities per request. The GC report shows 1,574 MB/sec allocation rate and 10.5% GC pause ratio, with peak heap at 2.5 GB.

## Theory

With ~2,000 seeded reviews and 1,000 products (see `SeedData.cs:86-101` and `SeedData.cs:37-49`), each Detail page request materializes ~3,000 entities with full EF change tracking. At 500 VUs, this means millions of entities tracked per second. Each tracked entity requires:
- Object allocation for the entity itself
- Identity map dictionary insertions (`StateManager.StartTrackingFromQuery`)
- Navigation fixup (`NavigationFixer.InitialFixup`)

This is the primary driver of the 1,574 MB/sec allocation rate and the severely inverted GC generation distribution (100 Gen2 vs 5 Gen0 collections). The massive short-lived allocations fill the Server GC's large Gen0 budget, then trigger expensive Gen2 collections with average 53ms pauses — directly inflating p95 latency.

## Proposed Fixes

1. **Server-side filtering with AsNoTracking for reviews (line 34-37):** Replace the full table scan with `_context.Reviews.AsNoTracking().Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToListAsync()`. This pushes the WHERE clause to SQL, returning only 1-7 reviews instead of ~2,000.

2. **Server-side filtering with AsNoTracking for related products (line 41-46):** Replace with `_context.Products.AsNoTracking().Where(p => p.Category == Product.Category && p.Id != id).Take(4).ToListAsync()`. This returns 4 products instead of 1,000. The random ordering can be dropped (or use a DB-side approach).

3. **AsNoTracking on product lookup (line 30):** Change `FindAsync(id)` to `_context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id)` since the product is read-only.

## Expected Impact

- **Allocation reduction:** ~3,000 tracked entities → ~15 untracked entities per request (~99.5% reduction for this endpoint)
- **p95 latency:** Estimated 15-25% improvement. Fewer allocations → fewer Gen2 collections → fewer 53-239ms GC pauses
- **RPS:** Estimated 15-20% improvement from reduced CPU time in TDS parsing, tracking, and GC
- **GC pause ratio:** Should drop significantly as this endpoint is one of the largest allocators

