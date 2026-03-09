# Product search and category filter load entire tables client-side

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:49-58`, `GetProductsByCategory` loads ALL categories AND ALL products:

```csharp
var categories = await _context.Categories.ToListAsync();
var matchingCategory = categories.FirstOrDefault(c =>
    c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At lines 69-77, `SearchProducts` loads all products then filters in memory:

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

## Theory

Product browsing by category and search are core read-heavy operations that likely dominate load test traffic. `GetProductsByCategory` makes two full table scans (Categories + Products) when it only needs one filtered query. The Products table materializes every row including Description text, consuming memory and network bandwidth unnecessarily.

`SearchProducts` pulls the entire Products table for every search, then filters in .NET. SQL Server's `LIKE` operator (via EF `Contains`) would push this work to the database where indexes on Name could help, and only matching rows would be transferred over the wire.

## Proposed Fixes

1. **Push filters to SQL:** For `GetProductsByCategory`, use `_context.Products.Where(p => p.Category == categoryName)` directly (SQL collation handles case-insensitivity). Validate category existence with `AnyAsync` instead of loading all categories. For `SearchProducts`, use `_context.Products.Where(p => EF.Functions.Like(p.Name, $"%{q}%") || ...)` to filter server-side.

2. **Add index on Product.Category:** Configure `entity.HasIndex(e => e.Category)` in OnModelCreating to speed up category-based filtering.

## Expected Impact

- p95 latency: ~5-10% reduction. Product catalog is likely a fixed-size dataset (not growing during test), so the impact is less dramatic than Cart/Orders, but eliminating full materialization still saves significant time.
- RPS: ~5-8% increase. Reduced memory allocation per request lowers GC pressure and frees thread pool threads faster.
- The Products table is read-heavy and stable-sized, so the improvement is consistent but moderate compared to the growing Cart/Order tables.
