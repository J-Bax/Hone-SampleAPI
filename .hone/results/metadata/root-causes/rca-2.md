# Server-side filtering for category and search queries

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:49-58`, `GetProductsByCategory` loads ALL categories and ALL products into memory:

```csharp
var categories = await _context.Categories.ToListAsync(); // line 49
var matchingCategory = categories.FirstOrDefault(...);
var allProducts = await _context.Products.ToListAsync(); // line 56
var filtered = allProducts.Where(p => p.Category.Equals(...)).ToList(); // line 57-58
```

Similarly at lines 69-77, `SearchProducts` loads all products then filters in memory:

```csharp
var allProducts = await _context.Products.ToListAsync(); // line 69
allProducts = allProducts.Where(p => p.Name.Contains(q, ...)).ToList();
```

## Theory

Both endpoints transfer the entire Products table from the database on every call, then discard most rows via in-memory filtering. As the product count grows, this wastes both DB I/O and memory allocation bandwidth. Under 36 concurrent VUs, each issuing both a category lookup and a search, the DB is serving full table scans unnecessarily. Server-side `WHERE` clauses let the DB return only matching rows, reducing data transfer and GC pressure.

## Proposed Fixes

1. **Category lookup:** Replace the two `ToListAsync()` calls with a single filtered query: `_context.Products.Where(p => p.Category == categoryName).ToListAsync()`. Use `_context.Categories.FirstOrDefaultAsync(c => c.Name == categoryName)` for the existence check.
2. **Search:** Use `_context.Products.Where(p => EF.Functions.Like(p.Name, $"%{q}%")).ToListAsync()` to push the filter to SQL.

## Expected Impact

- p95 latency: ~2-4ms reduction per affected request
- Reduces memory allocations and GC pauses
- Overall p95 improvement: ~2-3% (these two endpoints represent ~10% of traffic combined)