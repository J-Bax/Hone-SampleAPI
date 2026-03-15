# Root Cause Analysis — Experiment 15

> Generated: 2026-03-14 19:30:03 | Classification: narrow — The optimization modifies only GetProductsByCategory and SearchProducts method bodies to use the existing _cachedProducts cache, requiring no changes to dependencies, database schema, API contracts, or other files.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 453.409225ms | 2054.749925ms |
| Requests/sec | 1420.3 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# SearchProducts and GetProductsByCategory bypass existing product cache

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:14-17`, `GetProducts()` maintains a 30-second in-memory cache:

```csharp
private static List<Product>? _cachedProducts;
private static DateTime _cacheExpiry = DateTime.MinValue;
private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
```

However, `SearchProducts` and `GetProductsByCategory` bypass this cache entirely.

At `ProductsController.cs:91`, the empty-query path loads all products with change tracking:

```csharp
var all = await _context.Products.ToListAsync();
```

At `ProductsController.cs:96-99`, the k6 scenario sends `q=Product` which matches all ~1000 products, materializing them with full change tracking:

```csharp
var results = await _context.Products
    .Where(p => p.Name.ToLower().Contains(lowerQ) || ...)
    .ToListAsync();
```

At `ProductsController.cs:76-78`, the category filter also queries the DB independently:

```csharp
var filtered = await _context.Products
    .Where(p => p.Category.ToLower() == categoryName.ToLower())
    .ToListAsync();
```

The CPU profile confirms EF Core materialization (12.8% inclusive) and SQL string reading (`TryReadChar` 2.3%) dominate CPU. The memory profile shows 580 MB/sec allocation rate and a 92% Gen0→Gen1 promotion mid-life crisis. `StringConverter.Write` at 2.4% indicates large response payloads from serializing ~1000 Product entities including their Description fields.

## Theory

Under the k6 scenario, `GetProducts` keeps `_cachedProducts` warm (1420 RPS, 30s TTL). But `SearchProducts` and `GetProductsByCategory` — also called once each per VU iteration — ignore this cached data. Each independently queries SQL Server, materializes entities with change tracking (creating snapshot copies of every property, roughly doubling per-entity memory), decodes NVARCHAR columns character-by-character (`TryReadChar`), and serializes large response payloads.

For `q=Product`, all ~1000 products match. SearchProducts materializes the entire Product table with EF tracking snapshots — the same data already sitting in `_cachedProducts` without tracking overhead. This generates ~15 MB/s of unnecessary allocations just from this endpoint, contributing to the mid-life crisis where objects survive Gen0 collection but die in Gen1.

The category filter adds another ~100 tracked entity materializations per request (1000 products / 10 categories), plus a redundant `AnyAsync` round-trip to verify the category exists.

## Proposed Fixes

1. **Reuse product cache for search and category filter:** When `_cachedProducts` is warm (`_cachedProducts != null && DateTime.UtcNow < _cacheExpiry`), filter it in-memory using case-insensitive `string.Contains` / `string.Equals` instead of querying the database. For `SearchProducts`: filter the cached list by Name/Description. For `GetProductsByCategory`: filter cached list by Category. The category existence check can also be performed against the cached data. Fall back to the current DB path when the cache is cold. If the cache is cold, populate it (using the same locking pattern from `GetProducts`) before filtering.

## Expected Impact

- p95 latency: ~35ms reduction per affected request by eliminating DB round-trips, entity materialization, and change-tracking overhead for ~1000 products
- Allocation reduction: ~30 MB/s less allocation volume, reducing Gen0/Gen1 collection frequency and 40.5ms max GC pause spikes
- Overall p95 improvement: estimated 3-5% from combined per-request latency savings and reduced GC tail-latency interference

