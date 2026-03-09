# Client-side filtering materializes all 1000 products for search and category queries

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:69`, `SearchProducts` loads the entire Products table (1000 rows) into memory before filtering:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

Then at lines 73-76, it filters in C#:

```csharp
allProducts = allProducts.Where(p =>
    p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
    (p.Description != null && p.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
).ToList();
```

Similarly, `GetProductsByCategory` at lines 49-58 performs two full-table loads:

```csharp
var categories = await _context.Categories.ToListAsync();
// ...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

The k6 scenario calls `GET /api/products/search?q=Product` and `GET /api/products/by-category/Electronics` on every VU iteration, plus `GET /api/products` also loads all 1000 products. This means 3 full Product table materializations per iteration (3000 Product objects).

## Theory

Each Product entity includes a Description field (a multi-sentence string per seed data at SeedData.cs:43-44), so materializing 1000 products allocates significant string data. Three full materializations per VU iteration at 500 VUs compounds the allocation pressure alongside the Reviews issue. The `StringComparison.OrdinalIgnoreCase` comparison forces client-side evaluation—EF Core cannot translate this to SQL—but SQL Server's default collation is case-insensitive, so a simple `EF.Functions.Like()` or `.Contains()` without `StringComparison` achieves the same result server-side.

The `GetProductsByCategory` method makes it worse by also loading all Categories, performing two round-trips and materializing two entire tables when a single `WHERE Category = @p0` query would suffice.

## Proposed Fixes

1. **Push search to SQL in SearchProducts:** Replace the in-memory filter with `_context.Products.Where(p => p.Name.Contains(q) || (p.Description != null && p.Description.Contains(q))).ToListAsync()`. EF Core translates parameterless `.Contains()` to SQL `LIKE '%value%'`, which uses SQL Server's default case-insensitive collation.

2. **Push category filter to SQL in GetProductsByCategory:** Replace the two-step approach with `_context.Products.Where(p => p.Category == categoryName).ToListAsync()`. Remove the separate Categories lookup entirely, or replace it with a targeted `AnyAsync` existence check.

## Expected Impact

- **Allocation volume:** Eliminates ~2000 Product entity materializations per VU iteration (search returns subset matching "Product" in name, category returns ~100 Electronics products vs. 1000 total each time). Estimated 15-25% reduction in total allocations.
- **p95 latency:** Expected reduction of 10-20% on top of the Reviews fix, as SQL Server returns smaller result sets and fewer objects traverse the serialization pipeline.
- **RPS:** Expected 10-15% additional throughput improvement from reduced memory pressure and faster query execution.
