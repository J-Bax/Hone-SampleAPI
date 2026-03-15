# Full Products table scan with in-memory filtering and no AsNoTracking

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:35`, the Products listing page loads all 1,000 products with full change tracking:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

Then at lines 38-51, category and search filtering are applied in memory:

```csharp
if (!string.IsNullOrWhiteSpace(category))
{
    allProducts = allProducts.Where(p =>
        p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
}
if (!string.IsNullOrWhiteSpace(q))
{
    allProducts = allProducts.Where(p =>
        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || ...).ToList();
}
```

Pagination at lines 57-60 also happens in memory after loading all rows:

```csharp
Products = allProducts.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();
```

No `AsNoTracking()` is used on any query.

## Theory

Every Products page visit materializes all 1,000 Product entities with full EF Core change tracking, then discards most of them (the page only shows 24 items). The k6 scenario always hits `GET /Products` without filters, so all 1,000 entities are materialized, tracked, and paginated in memory every time. The CPU profiler shows heavy costs in `StateManager.StartTrackingFromQuery` (~1,030 samples), `NavigationFixer.InitialFixup` (~1,087 samples), and `CastHelpers` (~18K samples) — all proportional to entity count. The change tracking overhead for a read-only page is entirely wasted.

This is the same pattern that was fixed in `ProductsController.cs` (experiment 1) for the API endpoint, but the Razor Page version was never optimized.

## Proposed Fixes

1. **Server-side filtering with IQueryable + AsNoTracking:** Build an `IQueryable<Product>` pipeline: start with `_context.Products.AsNoTracking()`, conditionally chain `.Where(p => p.Category == category)` and `.Where(p => p.Name.Contains(q) || p.Description.Contains(q))`, then apply `.Skip().Take()` for pagination before calling `ToListAsync()`. Use a separate `CountAsync()` on the filtered queryable for `TotalPages` calculation.

2. **Eliminate full table materialization:** The current code loads 1,000 entities to show 24. With server-side Skip/Take, SQL Server returns only the 24 needed rows, reducing materialization by ~97% and eliminating all associated change tracking, type casting, and serialization overhead.

## Expected Impact

- p95 latency: Per-request latency should drop ~30ms by materializing 24 entities instead of 1,000 and eliminating change tracking.
- Memory: ~97% reduction in per-request allocations from this endpoint.
- Overall p95 improvement: ~2.9% (5.6% traffic share × 30ms / 583ms).
