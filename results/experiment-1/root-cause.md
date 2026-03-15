# Root Cause Analysis — Experiment 1

> Generated: 2026-03-15 03:45:16 | Classification: narrow — The fix replaces full-table ToListAsync() calls with server-side .Where() filtering in GetProductsByCategory and SearchProducts, which is a single-file query optimization that does not change API contracts, dependencies, or schema.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 1596.242785ms | 1596.242785ms |
| Requests/sec | 468.5 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# Full table scans with in-memory filtering on Products

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

Three read endpoints load the entire 1,000-row Products table into memory with full change tracking:

**GetProducts** (`ProductsController.cs:25`):
```csharp
var products = await _context.Products.ToListAsync();
```
Returns all 1,000 products on every call — no pagination, no projection, no AsNoTracking().

**GetProductsByCategory** (`ProductsController.cs:49-58`):
```csharp
var categories = await _context.Categories.ToListAsync();
// ...
var allProducts = await _context.Products.ToListAsync();
var filtered = allProducts.Where(p =>
    p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
```
Performs TWO full table scans (Categories + Products), then filters in C# memory instead of SQL. Only ~100 of 1,000 products match any given category.

**SearchProducts** (`ProductsController.cs:69-77`):
```csharp
var allProducts = await _context.Products.ToListAsync();
if (!string.IsNullOrWhiteSpace(q))
{
    allProducts = allProducts.Where(p =>
        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || ...).ToList();
}
```
Loads all 1,000 products and filters in memory.

The CPU profiler confirms this: `ToListAsync` is 18.5% inclusive CPU, `SqlDataReader.TryReadColumnInternal` is 15.2%, and `NavigationFixer.InitialFixup` is 4.8% — all driven by materializing and change-tracking 1,000 entity instances per request. Each Product includes a Description string (~100 chars), causing significant `UnicodeEncoding.GetChars` overhead (1.5% exclusive CPU) from TDS string decoding.

The GC report shows 1,950 MB/sec allocation rate with an inverted generation distribution (232 Gen2 vs 9 Gen0). Loading 1,000 Product objects per request under 500 VUs means hundreds of concurrent List<Product> allocations with backing arrays well over 85KB — these go directly to the Large Object Heap, triggering the pathological Gen2 collection pattern.

## Theory

Under the k6 stress profile (500 concurrent VUs), these three endpoints collectively fire ~16.7% of all requests. Each request materializes 1,000 Product entities with full change tracking, meaning:

1. **SQL overhead**: TDS protocol parses ~7 columns × 1,000 rows = 7,000 column reads per request, saturating `TryReadColumnInternal`.
2. **Materialization overhead**: EF Core's `StartTrackingFromQuery` creates identity map entries, snapshots, and runs `NavigationFixer.InitialFixup` for all 1,000 entities — an O(n²) operation on the change tracker's SortedDictionary (3.4% exclusive CPU).
3. **Memory pressure**: Each `ToListAsync()` allocates a List<Product> with a ~24KB+ backing array (1,000 × 24 bytes reference), plus 1,000 individual Product heap objects with string fields. Under 500 VUs, this creates hundreds of simultaneous large allocations, overwhelming the GC.
4. **GetProductsByCategory** is doubly wasteful: it issues two independent full table scans when a single `WHERE Category = @p` query would suffice.

The combination of excessive data transfer, unnecessary change tracking, and in-memory filtering is the primary driver of the 1,596ms p95 latency and the 14.3% GC pause ratio.

## Proposed Fixes

1. **Push filtering to SQL + AsNoTracking()**: Replace `_context.Products.ToListAsync()` followed by LINQ-to-Objects with server-side `Where()` clauses on the IQueryable before calling `ToListAsync()`. Add `.AsNoTracking()` to all three read endpoints. For `GetProductsByCategory`, replace the two-query pattern with a single `_context.Products.AsNoTracking().Where(p => p.Category == categoryName)`. For `SearchProducts`, use `EF.Functions.Like()` or `IQueryable.Where(p => p.Name.Contains(q))` to push the filter to SQL. For `GetProducts`, add `.AsNoTracking()` at minimum.

2. **Eliminate redundant Categories scan**: In `GetProductsByCategory` (lines 49-51), the separate `_context.Categories.ToListAsync()` followed by `FirstOrDefault` is unnecessary — the category name is already provided as a route parameter. Remove the categories query and filter Products directly by the `categoryName` string.

## Expected Impact

- **p95 latency**: Estimated ~300ms reduction on affected requests. GetProductsByCategory drops from 2 full table scans to 1 filtered query returning ~100 rows. SearchProducts and GetProducts benefit from AsNoTracking() eliminating change-tracker overhead.
- **GC pressure**: Reducing materialized entity count from 1,000 to ~100 per category request eliminates ~90% of Product allocations on that endpoint. AsNoTracking() eliminates snapshot/identity-map allocations across all three endpoints.
- **Overall p95 improvement**: ~3.1% (16.7% traffic × 300ms / 1596ms). Real improvement likely higher due to reduced GC pressure benefiting all endpoints.

