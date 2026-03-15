# Root Cause Analysis — Experiment 1

> Generated: 2026-03-15 11:36:29 | Classification: narrow — The optimization replaces client-side ToListAsync() + LINQ filtering with server-side .Where() clauses in GetProductsByCategory and SearchProducts methods, modifying only query logic within method bodies of a single file without changing routes, response shapes, dependencies, or requiring any other file changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 7546.103045ms | 7546.103045ms |
| Requests/sec | 125.5 | 125.5 |
| Error Rate | 0% | 0% |

---
# Eliminate full-table product scans with server-side filtering

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

Every read endpoint in `ProductsController.cs` materializes the entire `Products` table (1,000 rows) into memory before applying any filter:

- **Line 25** (`GetProducts`): `var products = await _context.Products.ToListAsync();` — returns all 1,000 products with no pagination.
- **Lines 49 + 56** (`GetProductsByCategory`): loads ALL categories *and* ALL products, then filters in C#:
  ```csharp
  var categories = await _context.Categories.ToListAsync();
  // ...
  var allProducts = await _context.Products.ToListAsync();
  var filtered = allProducts.Where(p =>
      p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
  ```
- **Line 69** (`SearchProducts`): `var allProducts = await _context.Products.ToListAsync();` then filters with `Contains()` in memory.

The CPU profiler confirms this: `TdsParserStateObject.TryReadChar` is the #1 managed hotspot (1.84%), `UnicodeEncoding.GetCharCount` adds another 2.7%, and EF Core materialization (`SingleQueryingEnumerable.MoveNextAsync`) costs 0.67% exclusively — all driven by reading far too many rows. The GC report shows 37.3 GB total allocations and an inverted generation distribution (Gen2=25, Gen0=3), indicating massive per-request allocation volumes flooding the heap.

These 4 endpoints account for 4 of the 18 requests per k6 iteration (~22% of traffic). Each call allocates ~1,000 Product objects with full EF change tracking (identity map lookups, NavigationFixer, shadow properties), contributing an estimated 15-18% of total system allocations.

## Theory

Loading 1,000 rows per request when only a handful are needed wastes CPU on TDS parsing, string decoding, and EF materialization, but the catastrophic effect is on GC. The 37.3 GB of allocations over the test drives blocking Gen2 collections that pause the process for up to 8.8 seconds (84.8% GC pause ratio). Because every VU iteration triggers at least 4 full product table scans, the allocation rate overwhelms the GC, creating multi-second stop-the-world pauses that directly cause the 7,546 ms p95 latency.

Additionally, `GetProductsByCategory` makes TWO full table scans (categories + products) when a single `WHERE` clause would suffice. `SearchProducts` pulls all rows to do a case-insensitive `Contains` that SQL Server can handle with `LIKE`.

## Proposed Fixes

1. **Server-side filtering + AsNoTracking:** Replace `ToListAsync()` followed by LINQ-to-Objects with EF `IQueryable` filters that translate to SQL `WHERE` clauses. Add `.AsNoTracking()` to all read queries to eliminate change-tracking overhead (~1.1% CPU). Specifically:
   - `GetProducts` (line 25): Add `.AsNoTracking()` (and ideally server-side pagination, but that changes API contract).
   - `GetProductsByCategory` (lines 49-58): Replace both `ToListAsync()` calls with a single `_context.Products.AsNoTracking().Where(p => p.Category == categoryName).ToListAsync()`.
   - `SearchProducts` (lines 69-77): Use `_context.Products.AsNoTracking().Where(p => EF.Functions.Like(p.Name, $"%{q}%") || EF.Functions.Like(p.Description, $"%{q}%")).ToListAsync()`.

## Expected Impact

- **p95 latency:** Estimated 10-15% overall improvement (~750-1,100 ms reduction). The filtered endpoints will materialize ~10-100 rows instead of 1,000, reducing per-request allocations by ~90%. This directly reduces GC pressure system-wide.
- **RPS:** Should increase proportionally as GC pauses consume less wall-clock time.
- **Allocation volume:** Estimated 15-18% reduction in total allocations, which should meaningfully reduce Gen2 collection frequency.

