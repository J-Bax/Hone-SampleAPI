# Replace client-side filtering with server-side queries

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:49-58`, `GetProductsByCategory` loads **all** categories and **all** products into memory, then filters in C#:

```csharp
var categories = await _context.Categories.ToListAsync();           // line 49
var matchingCategory = categories.FirstOrDefault(c =>               // line 50
    c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
var allProducts = await _context.Products.ToListAsync();            // line 56
var filtered = allProducts.Where(p =>                               // line 57
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

At `ProductsController.cs:69-77`, `SearchProducts` loads **all 1000 products** then filters in memory:

```csharp
var allProducts = await _context.Products.ToListAsync();            // line 69
allProducts = allProducts.Where(p =>                                // line 73
    p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || ...
```

At `ProductsController.cs:25`, `GetProducts` loads all products with change tracking enabled:

```csharp
var products = await _context.Products.ToListAsync();               // line 25
```

The k6 baseline scenario hits **three** of these endpoints per VU iteration (lines 38, 52, 57 of `baseline.js`): `GET /api/products`, `GET /api/products/search?q=Product`, and `GET /api/products/by-category/Electronics`. Each materializes all 1000 Product entities with full EF Core change tracking.

CPU profiling shows 12% inclusive CPU in SQL TDS parsing and 2.7% in Unicode string decoding — indicating massive data transfer. GC analysis shows 227 GB allocated over 120s (~1.9 GB/sec) with an inverted Gen2-heavy collection profile, confirming huge per-request allocation volumes from materializing entire tables.

## Theory

With 500 concurrent VUs, each iteration triggers 3 full-table scans of the Products table (1000 rows × 3 = 3000 Product entities per iteration). SQL Server must serialize all rows over TDS, EF Core must deserialize Unicode strings (2.7% CPU in `UnicodeEncoding.GetCharCount`), construct entity objects, and track them in identity maps (2.2% CPU in change tracking). Each Product has `Name` (200 chars max), `Description` (long text), `Category`, and timestamps — substantial per-row payload.

At 683 RPS across ~13 requests per VU iteration, roughly 50 VU iterations/sec hit these three endpoints, materializing ~150,000 Product entities/sec. Each entity creates multiple heap objects (the entity, strings for Name/Description/Category), contributing heavily to the 1.9 GB/sec allocation rate and driving the inverted GC pattern (150 Gen2 vs 4 Gen0 collections).

## Proposed Fixes

1. **Server-side WHERE clauses:** In `GetProductsByCategory` (line 49-58), replace the two `ToListAsync()` calls with a single `_context.Products.Where(p => p.Category == categoryName).AsNoTracking().ToListAsync()`. In `SearchProducts` (line 69-77), use `_context.Products.Where(p => EF.Functions.Like(p.Name, $"%{q}%") || EF.Functions.Like(p.Description, $"%{q}%")).AsNoTracking().ToListAsync()` to push filtering to SQL.

2. **Add AsNoTracking() to all read endpoints:** Add `.AsNoTracking()` to `GetProducts` (line 25) and all other read paths. This eliminates the 2.2% CPU overhead from `StateManager.StartTrackingFromQuery`, `NavigationFixer.InitialFixup`, and identity map dictionary lookups.

## Expected Impact

- **p95 latency:** ~30-40% reduction (from 888ms toward ~530-620ms). Eliminating 3 full-table scans per iteration removes the bulk of SQL transfer time and GC pressure.
- **RPS:** ~40-50% increase. Less CPU spent on TDS parsing, string decoding, and change tracking means more requests served per second.
- **GC pressure:** Significant reduction in allocation rate — materializing ~100 filtered products instead of 3×1000 reduces per-request allocations by ~10x for these endpoints, directly lowering Gen2 collection frequency and pause times.
- **Error rate:** Should remain at 0%.
