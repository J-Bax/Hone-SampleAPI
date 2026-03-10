# Root Cause Analysis — Experiment 1

> Generated: 2026-03-09 21:21:54 | Classification: narrow — The optimization moves filtering from client-side (LINQ-to-Objects on full result set) to database-side (LINQ-to-SQL in query) by modifying method bodies in ProductsController.cs only, requiring no file changes, dependencies, migrations, or API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 894.68869ms | 894.68869ms |
| Requests/sec | 663.4 | 663.4 |
| Error Rate | 0% | 0% |

---
# Client-side filtering loads entire Products table on every request

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:25`, the `GetProducts` endpoint loads the full table:

```csharp
var products = await _context.Products.ToListAsync();
```

At `ProductsController.cs:49,56`, `GetProductsByCategory` loads ALL categories AND ALL products into memory just to filter one category:

```csharp
var categories = await _context.Categories.ToListAsync();
var matchingCategory = categories.FirstOrDefault(...);
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p => p.Category.Equals(categoryName, ...)).ToList();
```

At `ProductsController.cs:69`, `SearchProducts` loads the full table to search in memory:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

The k6 scenario calls 4 product endpoints per VU iteration (lines 38, 47, 52, 57 of baseline.js). With 500 concurrent VUs, this means ~2,000 concurrent full-table materializations of 1,000 products each. The CPU profile confirms: 8% in SqlClient TDS parsing, 2.1% in Unicode decoding, and 1.9% in EF Core change tracking — all driven by materializing massive result sets with tracking enabled.

The seed data creates 1,000 products with Description fields averaging ~90 characters each (`SeedData.cs:43-44`), making the string column cost especially high.

## Theory

Each request materializes all 1,000 Product entities with full change tracking, allocating entity objects, identity map entries, and navigation fixer state — even though these are read-only queries. With ~2,000 concurrent calls per iteration cycle, EF Core is materializing ~2 million Product entities per second. Each entity allocation includes the object itself, string properties (Name, Description, Category), and change tracking metadata. This is the dominant contributor to the 1,552 MB/sec allocation rate and the catastrophic GC pressure (24.4% pause ratio, 122 Gen2 collections).

The `GetProductsByCategory` endpoint is especially wasteful: it issues two full-table scans (Categories + Products) when a single server-side `WHERE` clause would suffice. `SearchProducts` with `q=Product` matches nearly every product (all names start with "Product"), so the in-memory filter saves nothing.

## Proposed Fixes

1. **Push filters to SQL with `.AsNoTracking()`:** In `GetProductsByCategory` (line 49-58), replace both `.ToListAsync()` calls with a single server-side query: `_context.Products.AsNoTracking().Where(p => p.Category == categoryName).ToListAsync()`. In `SearchProducts` (line 69-77), use `_context.Products.AsNoTracking().Where(p => EF.Functions.Like(p.Name, $"%{q}%") || EF.Functions.Like(p.Description, $"%{q}%")).ToListAsync()`. In `GetProducts` (line 25), add `.AsNoTracking()`.

2. **Add `.AsNoTracking()` to all read paths:** Lines 25, 49, 56, 69 — every query that returns data without modification should use `.AsNoTracking()` to eliminate the ~1.9% CPU overhead from change tracking (NavigationFixer, IdentityMap, InternalEntityEntry).

## Expected Impact

- **p95 latency:** ~40-50% reduction (from 894ms toward 450-550ms). Eliminating full-table materializations on the 4 most-called endpoints will dramatically reduce allocation volume and GC pauses.
- **RPS:** ~50-70% increase. Less GC pause time means more CPU available for request processing.
- **Allocation rate:** Estimated 60-70% reduction for product-related endpoints. `GetProductsByCategory` goes from materializing 1,000+ entities to ~100 (Electronics category). `SearchProducts` still returns many matches for `q=Product` but avoids change tracking overhead.
- **GC pressure:** Gen2 collections should decrease significantly as the ephemeral generations can keep up with reduced allocation rates.

