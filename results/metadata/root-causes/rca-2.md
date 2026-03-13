# Product search and category filter use client-side evaluation

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

**GetProductsByCategory (lines 49-58)** executes TWO full-table scans — one for categories and one for products — then filters both in memory:

```csharp
var categories = await _context.Categories.ToListAsync();
var matchingCategory = categories.FirstOrDefault(c =>
    c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
// ...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

**SearchProducts (lines 69-76)** loads all products and filters in memory:

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

**GetProducts (line 25)** loads the entire products table with no pagination:

```csharp
var products = await _context.Products.ToListAsync();
```

These endpoints are called on every VU iteration (3 of 13 requests, plus Razor pages at `Pages/Products/Index.cshtml.cs:35` and `Pages/Index.cshtml.cs:28` also load all products).

## Theory

Every product-related request materializes the entire `Products` table into .NET objects, consuming memory and DB bandwidth. Under 500 concurrent VUs, this means 500 simultaneous full-table scans competing for SQL Server resources. The `by-category` endpoint is especially wasteful — it scans the Categories table just to validate a name, then scans the entire Products table to filter by a string column that SQL Server could filter with a simple WHERE clause. The search endpoint similarly pulls all rows across the network and applies `String.Contains` in C# rather than using SQL `LIKE` or `CONTAINS`.

## Proposed Fixes

1. **Push category filter to SQL:** Replace the two-query pattern in `GetProductsByCategory` with `_context.Products.Where(p => p.Category == categoryName).ToListAsync()`. Use `EF.Functions.Collate()` or `ToLower()` for case-insensitive matching if needed. The category existence check can be `AnyAsync` instead of loading all categories.

2. **Push search to SQL:** In `SearchProducts`, use `EF.Functions.Like()` or `.Where(p => p.Name.Contains(q))` on the `IQueryable` before calling `ToListAsync()`, so SQL Server handles the filtering.

## Expected Impact

- **p95 latency:** Category filter should drop ~80-120ms (eliminating 2 full scans → 1 targeted query). Search should drop ~50-80ms.
- **Memory/GC:** Significantly reduced object allocations per request since only matching rows are materialized.
- **Overall p95 improvement:** ~8-12% (estimated ~90ms average reduction across ~23% of traffic).
