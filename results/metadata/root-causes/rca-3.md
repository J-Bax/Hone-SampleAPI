# Full-table Products loads for search and category filtering

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:49-58`, `GetProductsByCategory` performs two unnecessary full-table loads:

```csharp
var categories = await _context.Categories.ToListAsync();
var matchingCategory = categories.FirstOrDefault(c =>
    c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At lines 69-77, `SearchProducts` loads all 1000 products and filters client-side:

```csharp
var allProducts = await _context.Products.ToListAsync();
if (!string.IsNullOrWhiteSpace(q))
{
    allProducts = allProducts.Where(p =>
        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        (p.Description != null && p.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
    ).ToList();
}
```

The baseline scenario calls both endpoints every iteration (baseline.js lines 52 and 57). The search uses `q=Product` which matches all 1000 products, so the client-side filter has no reduction effect — it loads 1000 products, filters, and returns 1000 products.

## Theory

With 1000 products (each containing Name, Description, Price, Category, timestamps), the `List<Product>` from `ToListAsync()` is approximately 200-400KB — well above the 85KB LOH threshold. Every VU iteration triggers these loads twice (search + category). Under 500 VUs, that's ~1000 LOH allocations per second from Products table loads alone.

`GetProductsByCategory` is doubly wasteful: it loads ALL categories (small but unnecessary) then ALL products, when a single `WHERE Category = 'Electronics'` query would return only ~100 products (1000 products / 10 categories).

The search endpoint's case-insensitive `Contains` with `StringComparison.OrdinalIgnoreCase` cannot be translated to SQL by EF Core, which is why the developer likely chose client-side evaluation. However, SQL Server's default collation is case-insensitive, so `EF.Functions.Like` or a simple `.Contains(q)` would achieve the same result server-side.

## Proposed Fixes

1. **Server-side category filter:** Replace both full-table loads with a single query: `_context.Products.Where(p => p.Category == categoryName).ToListAsync()`. For the category existence check, use `_context.Categories.AnyAsync(c => c.Name == categoryName)` or `FirstOrDefaultAsync`.

2. **Server-side search:** Replace the client-side filter with `_context.Products.Where(p => p.Name.Contains(q) || (p.Description != null && p.Description.Contains(q))).ToListAsync()`. EF Core translates `.Contains(string)` to SQL `LIKE '%value%'`, and SQL Server's default collation handles case-insensitivity.

## Expected Impact

- p95 latency: ~10-15% reduction. Category endpoint drops from loading 1000 products to ~100. Search still returns ~1000 for `q=Product` but eliminates the intermediate full materialization + re-filter.
- RPS: ~10-15% increase from reduced SQL data transfer and fewer LOH allocations.
- Memory: Each category request allocates ~90% less memory. Search queries can leverage future SQL indexes on Name/Category columns.
