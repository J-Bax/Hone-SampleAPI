# Product search and category filter scan full Products table in memory

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:49-58`, `GetProductsByCategory` loads ALL categories, then ALL products, and filters both in C#:

```csharp
var categories = await _context.Categories.ToListAsync();
var matchingCategory = categories.FirstOrDefault(c =>
    c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At lines 69-77, `SearchProducts` loads ALL products then filters with `Contains` in memory:

```csharp
var allProducts = await _context.Products.ToListAsync();
if (!string.IsNullOrWhiteSpace(q))
{
    allProducts = allProducts.Where(p =>
        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ...
    ).ToList();
}
```

The main `stress.js` scenario calls both endpoints (`/api/products/search?q=Product` and `/api/products/by-category/Electronics`) in its traffic mix (lines 37-38), and the `stress-products.js` teardown calls search to clean up orphaned products.

## Theory

Both endpoints issue `SELECT * FROM Products` and transfer every row to the application process, even though only a subset is needed. As `stress-products.js` creates and sometimes orphans products, the table grows during the test run. Each request materializes every Product entity (allocations, deserialization) only to discard most rows. This wastes database I/O, network bandwidth, and GC pressure on the app server.

For `SearchProducts`, SQL Server can evaluate `LIKE '%Product%'` far more efficiently than loading all rows and running `String.Contains` in C#, especially with a large result set.

## Proposed Fixes

1. **Push `GetProductsByCategory` filter to SQL:** Replace the two `ToListAsync()` calls with `_context.Categories.FirstOrDefaultAsync(c => c.Name == categoryName)` and `_context.Products.Where(p => p.Category == categoryName).ToListAsync()`. Use `EF.Functions.Like` or `EF.Functions.Collate` for case-insensitive matching if needed.

2. **Push `SearchProducts` filter to SQL:** Replace the in-memory filter with `_context.Products.Where(p => EF.Functions.Like(p.Name, $"%{q}%") || EF.Functions.Like(p.Description, $"%{q}%")).ToListAsync()`.

## Expected Impact

- p95 latency: **15-25% reduction** ΓÇö These endpoints are part of the general stress mix. Eliminating full scans reduces per-request time and frees connection pool capacity.
- RPS: **10-20% improvement** ΓÇö Less data transferred per query means faster DB round-trips and reduced GC pressure.
- Error rate: Remains at 0%.
