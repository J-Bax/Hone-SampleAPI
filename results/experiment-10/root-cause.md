# Root Cause Analysis — Experiment 10

> Generated: 2026-03-10 02:53:26 | Classification: narrow — The proposed optimization (adding AsNoTracking to queries and optimizing the GetCategory product query) modifies only implementation internals within this single file—no dependencies, no migrations, no API contract changes, no test modifications required.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 408.6216ms | 888.549155000001ms |
| Requests/sec | 1345.6 | 683.2 |
| Error Rate | 0% | 0% |

---
# Add AsNoTracking and optimize GetCategory product query

> **File:** `SampleApi/Controllers/CategoriesController.cs` | **Scope:** narrow

## Evidence

Both endpoints in `CategoriesController.cs` lack `AsNoTracking`, and `GetCategory` uses string-based filtering without projection:

`CategoriesController.cs:25-26`:
```csharp
var categories = await _context.Categories.ToListAsync();
```
No `AsNoTracking()` — all Category entities are tracked with change-detection snapshots despite being a read-only endpoint.

`CategoriesController.cs:35-42`:
```csharp
var category = await _context.Categories.FindAsync(id);
// ...
var products = await _context.Products
    .Where(p => p.Category == category.Name)
    .ToListAsync();
```

Two issues:
1. `FindAsync(id)` loads a tracked Category entity solely to extract its `Name` string for the subsequent query
2. The Products query loads all columns (including `Description`) with change tracking for every matching product — potentially ~100 products for a popular category

The CPU profile notes: `StringConverter.Write` at 0.74% and `ObjectDefaultConverter.OnTryWrite` at 1.69% indicate large object graphs being serialized. Tracked entities add overhead through `UnicodeEncoding.GetCharCount/GetChars` (0.66%) by materializing all string columns including `Description`.

## Theory

Change tracking doubles the memory footprint of each loaded entity: EF Core stores both the entity and an internal snapshot for dirty-checking. For the Products query returning ~100 Product entities with `Description` (each ~80+ chars), this means ~200 object allocations instead of ~100. While `/api/categories` endpoints are not the primary load test traffic, the missing `AsNoTracking` pattern is inconsistent with the rest of the codebase (ProductsController, OrdersController, detail page all use it) and the tracked entities contribute to GC pressure — the Gen1/Gen0 ratio of 0.88 suggests medium-lifetime objects surviving Gen0, consistent with tracked entities held for the request scope.

## Proposed Fixes

1. **Add `AsNoTracking()` to both queries** — `GetCategories` at line 25 and the products query in `GetCategory` at line 40:
   ```csharp
   var categories = await _context.Categories.AsNoTracking().ToListAsync();
   ```
   ```csharp
   var products = await _context.Products.AsNoTracking()
       .Where(p => p.Category == category.Name)
       .ToListAsync();
   ```

2. **Replace `FindAsync` with `AsNoTracking().FirstOrDefaultAsync`** at line 35 to avoid tracking the Category entity.

## Expected Impact

- **p95 latency**: −2 to 5ms (marginal direct impact since these endpoints are not primary load test targets).
- **Allocation rate**: −3–5 MB/sec from eliminating change-tracking snapshots, improving GC behavior.
- **Code consistency**: Aligns with the `AsNoTracking` pattern established across all other read-only endpoints, preventing regression if these endpoints are added to load test scenarios.

