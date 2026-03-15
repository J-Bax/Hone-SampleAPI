# Root Cause Analysis — Experiment 5

> Generated: 2026-03-15 05:40:43 | Classification: narrow — The full table scans of Reviews (line 34) and Products (line 41) can be fixed by adding server-side .Where() filters directly in the LINQ queries within this single file, replacing client-side filtering with database-side filtering — no API contract, dependency, or multi-file changes needed.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 1555.044615ms | 1596.242785ms |
| Requests/sec | 484.7 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# Full table scans of Reviews and Products in product detail page

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:34-36`, the GET handler loads ALL ~2000 reviews into memory and filters client-side:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
Reviews = allReviews.Where(r => r.ProductId == id)
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList();
```

At lines 41-46, it loads ALL 1000 products for the related-products feature:

```csharp
var allProducts = await _context.Products.ToListAsync();
RelatedProducts = allProducts
    .Where(p => p.Category == Product.Category && p.Id != id)
    .OrderBy(_ => Guid.NewGuid())
    .Take(4)
    .ToList();
```

The POST handler at line 65 adds another full table scan:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
```

And at line 88, the POST re-invokes `OnGetAsync()`, re-loading all 3000+ entities a second time.

## Theory

Each GET request materializes ~3000 entities (2000 reviews + 1000 products) with full change tracking, only to use ~10-20 of them. Each POST materializes ~6000+ (CartItems + 2× Reviews + 2× Products). This is a primary driver of the 206 GB total allocation and 222 Gen2 GC collections observed in profiling.

The CPU profile confirms this: 25% inclusive CPU in `ToListAsync()`, 5.6% in `NavigationFixer.InitialFixup` (change tracking), 3% in SortedSet enumeration (identity map), and 2.3% in CastHelpers (entity materialization) are all proportional to entity count. With 500 VUs hitting this page back-to-back, the app materializes millions of entities per second, overwhelming the GC.

The `Guid.NewGuid()` random ordering at line 44 cannot be pushed to SQL, so it forces client-side evaluation of all same-category products.

## Proposed Fixes

1. **Server-side review filtering:** Replace the full Reviews scan at line 34 with `_context.Reviews.Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToListAsync()`. This reduces ~2000 rows to typically 1-7.

2. **Server-side related products query:** Replace the full Products scan at line 41 with a server-side query using `_context.Products.Where(p => p.Category == Product.Category && p.Id != id).Take(4).ToListAsync()`. Sacrifice random ordering for a massive reduction in data transfer and allocation. Alternatively use `OrderBy(p => p.Id % someValue)` for pseudo-random server-side ordering.

3. **Server-side CartItems filter in POST:** Replace line 65 with `_context.CartItems.Where(c => c.SessionId == sessionId && c.ProductId == productId).FirstOrDefaultAsync()`.

## Expected Impact

- p95 latency: ~300ms reduction per request (from materializing 3000 entities down to ~10-20)
- Allocation: ~99% reduction per request on this endpoint, significantly easing GC pressure
- Overall p95 improvement: ~2.2% (11.1% of traffic * 300ms / 1527ms p95)
- GC: fewer Gen2 collections due to dramatically lower allocation volume

