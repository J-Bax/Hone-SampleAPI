# GetCategory queries products by string without AsNoTracking

> **File:** `SampleApi/Controllers/CategoriesController.cs` | **Scope:** narrow

## Evidence

At `CategoriesController.cs:40-42`, `GetCategory` queries products by category name string:

```csharp
var products = await _context.Products
    .Where(p => p.Category == category.Name)
    .ToListAsync();
```

This query and all other read-only GET endpoints across the application (e.g., `GetOrders` at `OrdersController.cs:25`, `GetProducts` at `ProductsController.cs:25`, `GetReviews` at `ReviewsController.cs:25`, `GetCategories` at `CategoriesController.cs:25`) load entities with full EF change tracking enabled. For `GetCategory`, this means up to ~100 Product entities (1000 products / 10 categories) are tracked unnecessarily.

Additionally, `CategoriesController.cs:35` uses `FindAsync` for the category lookup:

```csharp
var category = await _context.Categories.FindAsync(id);
```

Then passes `category.Name` to filter Products. This two-step lookup could be a single query.

## Theory

EF Core's change tracker creates a snapshot of every materialized entity to detect modifications at `SaveChanges` time. For read-only endpoints that never call `SaveChanges`, this is pure overhead ΓÇö extra memory allocations, identity-resolution hash lookups, and GC pressure. Under concurrent load, the additional allocations per request compound into measurable p95 latency increases, especially for endpoints returning large result sets like `GetProducts` (1000 rows) or `GetReviews` (~2000 rows).

The `CategoriesController.GetCategory` is the best representative because it both loads a non-trivial number of Products (~100 per category) with tracking AND this specific endpoint hasn't been optimized yet.

## Proposed Fixes

1. **Add AsNoTracking to read-only queries in CategoriesController:** At `CategoriesController.cs:25`, change to `_context.Categories.AsNoTracking().ToListAsync()`. At `CategoriesController.cs:40`, change to `_context.Products.AsNoTracking().Where(...).ToListAsync()`. Replace the `FindAsync` at line 35 with `_context.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id)` since `FindAsync` always tracks.

2. **Optionally combine into single query:** Replace the two-step lookup (find category, then query products) with a single `Products.AsNoTracking().Where(p => p.Category == categoryName)` query, skipping the category lookup entirely since the category name is already in the route parameter ΓÇö but only if the 404 behavior for unknown categories is preserved by checking the result.

## Expected Impact

- p95 latency: ~3-5% reduction. Change tracking overhead is per-entity, so eliminating it for ~100 products per GetCategory call (and ~1000 for GetProducts, etc.) reduces allocation pressure.
- RPS: ~3-5% increase. Less GC pressure and fewer allocations per request improve throughput under sustained load.
- Error rate: No change.
