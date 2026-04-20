# Eliminate full-table loads in product detail page

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Detail.cshtml.cs:34-37`, ALL reviews are loaded to find those for one product:

```csharp
var allReviews = await _context.Reviews.ToListAsync(); // line 34
Reviews = allReviews.Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToList();
```

At lines 41-46, ALL products are loaded to find related ones:

```csharp
var allProducts = await _context.Products.ToListAsync(); // line 41
RelatedProducts = allProducts.Where(p => p.Category == Product.Category && p.Id != id)...
```

## Theory

The detail page is hit once per VU iteration. Under load, each request materializes the entire Reviews and Products tables into memory just to filter down to a handful of rows. This creates unnecessary DB I/O, memory pressure, and GC pauses. Server-side filtering would return only the needed rows.

## Proposed Fixes

1. **Filter reviews server-side:** Replace with `_context.Reviews.Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToListAsync()`.
2. **Filter related products server-side:** Replace with `_context.Products.Where(p => p.Category == Product.Category && p.Id != id).Take(4).ToListAsync()`.

## Expected Impact

- p95 latency: ~2-3ms reduction per detail page request
- Reduces memory allocations significantly
- Overall p95 improvement: ~1-2%