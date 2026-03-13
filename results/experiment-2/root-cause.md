# Root Cause Analysis — Experiment 2

> Generated: 2026-03-13 16:59:07 | Classification: narrow — Fix loads all reviews (line 34) and all products (line 41) into memory, then filters in LINQ-to-Objects; optimizing these queries to filter at the database level requires only modifying method bodies in this single file without adding dependencies, migrations, or changing API contracts.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 888.549155000001ms | 888.549155000001ms |
| Requests/sec | 683.2 | 683.2 |
| Error Rate | 0% | 0% |

---
# Product detail page loads all reviews and all products into memory

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Detail.cshtml.cs:34-36`, the product detail page loads every review in the database:

```csharp
var allReviews = await _context.Reviews.ToListAsync();           // line 34
Reviews = allReviews.Where(r => r.ProductId == id)               // line 35
                    .OrderByDescending(r => r.CreatedAt).ToList(); // line 36
```

Then at lines 41-46, it loads every product to find 4 related items:

```csharp
var allProducts = await _context.Products.ToListAsync();         // line 41
RelatedProducts = allProducts
    .Where(p => p.Category == Product.Category && p.Id != id)    // line 43
    .OrderBy(_ => Guid.NewGuid()).Take(4).ToList();              // line 44-46
```

The seed data contains ~2,000 reviews and 1,000 products. This single page load materializes ~3,000 entities with full change tracking to display perhaps 5 reviews and 4 related products.

The k6 scenario hits `GET /Products/Detail/{randomId}` every iteration (7.7% of total traffic). Combined with the API review endpoints, this means the full Reviews table is loaded into memory 4 times per iteration.

## Theory

Each detail page request triggers two unbounded queries returning ~3,000 total entities through EF Core's full materialization pipeline. The `List<Review>` of 2,000 entries (each with a Comment string up to 2,000 chars) easily exceeds 85 KB, landing on the Large Object Heap. The `List<Product>` of 1,000 entries with Description fields similarly hits LOH.

Both lists are immediately filtered down to a handful of items and discarded, but not before the GC must track and eventually collect ~3,000 entity objects plus their backing change-tracker structures (identity maps, navigation fixup dictionaries). At 52+ iterations/second, this page alone produces ~156,000 wasted entity materializations per second.

The `OrderBy(_ => Guid.NewGuid())` at line 44 for random selection also forces a full sort of the in-memory list — this should be done server-side or via a more efficient random sampling approach.

## Proposed Fixes

1. **Server-side review filtering (lines 34-37):** Replace with `Reviews = await _context.Reviews.AsNoTracking().Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToListAsync();`. This pushes both the WHERE and ORDER BY to SQL, returning only the handful of reviews for that product.

2. **Server-side related products query (lines 41-46):** Replace with a server-side filtered query: `RelatedProducts = await _context.Products.AsNoTracking().Where(p => p.Category == Product.Category && p.Id != id).Take(4).ToListAsync();`. The random ordering can be achieved with `OrderBy(p => EF.Functions.Random())` if supported, or simply omitted (taking any 4 same-category products is acceptable).

## Expected Impact

- p95 latency: ~10-13% overall reduction. Eliminating ~2,980 wasted entity materializations per iteration (23% of total) further reduces LOH pressure and Gen2 frequency.
- Per-request latency for the detail page: ~250-350ms reduction by avoiding materialization of ~3,000 entities.
- Combined with Opportunity 1, the Reviews table full-scan pattern is eliminated from 4 of the 4 call sites that load it per iteration.

