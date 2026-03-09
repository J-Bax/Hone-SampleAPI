# Client-side filtering loads entire Products and Categories tables on every request

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

Three endpoints in `ProductsController.cs` pull full tables into application memory and filter client-side:

**`SearchProducts` (line 69):**
```csharp
var allProducts = await _context.Products.ToListAsync();
```
Loads every product row, then filters in-memory at lines 73-76.

**`GetProductsByCategory` (lines 49 + 56):**
```csharp
var categories = await _context.Categories.ToListAsync();
// ...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```
Two full table scans (Categories + Products), then client-side filtering.

**`GetProducts` (line 25):**
```csharp
var products = await _context.Products.ToListAsync();
```
No pagination — returns every product.

The k6 baseline scenario calls all three of these endpoints plus `GetProduct(id)` on every single VU iteration (see `baseline.js` lines 38, 52, 57), so under 500 VUs this generates ~2,000 full table scans per second across these three endpoints alone.

## Theory

When `ToListAsync()` is called before any `Where` clause, EF Core issues `SELECT * FROM Products` (or Categories) with no server-side predicate. The entire result set is materialized into .NET objects, transferred over the LocalDB named-pipe connection, and only then filtered in C# LINQ-to-Objects. Under high concurrency (500 VUs), this creates:

1. **Excessive SQL Server I/O** — full table scans saturate the LocalDB buffer pool
2. **Excessive memory allocation** — every request allocates a `List<Product>` containing all rows, increasing GC pressure
3. **Wasted network transfer** — all columns of all rows traverse the pipe even though most are discarded

With the baseline sending 4 product-related requests per VU iteration, this is the single largest contributor to the 813ms p95 latency.

## Proposed Fixes

1. **Push predicates to the database:** In `SearchProducts`, use `_context.Products.Where(p => EF.Functions.Like(p.Name, $"%{q}%") || EF.Functions.Like(p.Description, $"%{q}%"))` to push the filter to SQL. In `GetProductsByCategory`, replace the two-query pattern with `_context.Products.Where(p => p.Category == categoryName)` using a direct `EF.Functions.Collate` or normalised string comparison, and validate the category name with a single `AnyAsync` instead of loading all categories.

2. **Add a database index on `Product.Category`:** In `AppDbContext.OnModelCreating` (line 23-29), add `.HasIndex(e => e.Category)` to the Product entity configuration so the server-side WHERE on Category can use an index seek instead of a scan.

## Expected Impact

- **p95 latency:** ~30-40% reduction (estimated 480-570ms). These three endpoints are hit on every VU iteration and currently account for 4 out of 13 HTTP requests, each doing full table scans.
- **RPS:** ~25-35% increase. Reduced SQL I/O and memory allocation frees server capacity.
- **Error rate:** No change expected (currently 0%).
