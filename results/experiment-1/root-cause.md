# Root Cause Analysis — Experiment 1

> Generated: 2026-03-09 22:42:06 | Classification: narrow — Replaces client-side filtering with server-side queries and AsNoTracking in existing controller methods; modifies only this single file's method bodies and query logic without changing routes, schemas, dependencies, or requiring migrations.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 888.549155000001ms | 888.549155000001ms |
| Requests/sec | 683.2 | 683.2 |
| Error Rate | 0% | 0% |

---
# Replace client-side filtering with server-side queries and AsNoTracking in ProductsController

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

The k6 scenario hits 4 ProductsController endpoints every VU iteration (list, get, search, by-category). Three of these load the entire 1000-row Products table into memory:

`ProductsController.cs:25`:
```csharp
var products = await _context.Products.ToListAsync();
```

`ProductsController.cs:69`:
```csharp
var allProducts = await _context.Products.ToListAsync();
```

`ProductsController.cs:49-57`:
```csharp
var categories = await _context.Categories.ToListAsync();
// ...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```

All queries use change tracking (no `AsNoTracking()`), and `SearchProducts` / `GetProductsByCategory` perform filtering in C# after materializing every row.

## Theory

With 500 concurrent VUs, each iteration triggers 3–4 requests that materialize all 1000 Product entities with full EF Core change tracking (identity map, NavigationFixer, type casting — the top CPU hotspots). Each Product entity includes `Name` (200 chars), `Description` (nvarchar, ~100+ chars), and several other columns. Per request, that's ~1000 tracked entities × ~500 bytes = ~500KB of managed objects going through the EF materialization pipeline.

At peak load, this creates enormous GC pressure — explaining the 199GB total allocation, 2.3GB peak heap, inverted Gen2-dominant collection pattern (129 Gen2 vs 7 Gen0), and 25.4% GC pause ratio. The CPU profile confirms this: `SingleQueryingEnumerable.MoveNextAsync` at 18% inclusive, `UnicodeEncoding.GetChars` at 2.1%, `SortedDictionary` enumeration (EF change tracking internals) at 3%, `CastHelpers` at 2.8%, and `NavigationFixer` at 1.5% — all driven by materializing and tracking 1000 entities per query.

Since `SearchProducts` and `GetProductsByCategory` filter client-side, SQL Server sends all 1000 rows over the wire every time even though only a subset is needed.

## Proposed Fixes

1. **Add `AsNoTracking()` to all read-only queries** in `GetProducts()` (line 25), `SearchProducts()` (line 69), and `GetProductsByCategory()` (line 56). This eliminates change tracking overhead (identity maps, NavigationFixer, SortedDictionary enumeration) and reduces per-entity memory.

2. **Move filtering to SQL** — In `SearchProducts()` (line 69-77), use `_context.Products.AsNoTracking().Where(p => p.Name.Contains(q) || p.Description.Contains(q))` to push the search to SQL Server via `LIKE`. In `GetProductsByCategory()` (line 49-58), replace the two-query pattern with a single `_context.Products.AsNoTracking().Where(p => p.Category == categoryName)` query, verifying the category exists via `AnyAsync` on `Categories` instead of loading all categories.

## Expected Impact

- **p95 latency**: ~30-40% reduction (from 888ms). These endpoints are hit on every VU iteration; eliminating change tracking and reducing SQL transfer for filtered endpoints will cut materialization CPU time and dramatically reduce GC pressure.
- **RPS**: ~40-60% increase. Less CPU time per request means higher throughput.
- **GC**: Significant reduction in Gen2 collections. AsNoTracking avoids identity map allocations; server-side filtering reduces entity count by 90% for filtered endpoints.

