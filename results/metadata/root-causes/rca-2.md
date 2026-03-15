# Serve single product lookups from the existing in-memory cache

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `Controllers/ProductsController.cs:56`, the `GetProduct` endpoint always hits the database:

```csharp
var product = await _context.Products.FindAsync(id);
```

However, the same controller already maintains a comprehensive in-memory cache of all 1,000 products (lines 14–17):

```csharp
private static List<Product>? _cachedProducts;
private static DateTime _cacheExpiry = DateTime.MinValue;
private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
```

This cache is populated by `GetOrPopulateCacheAsync()` (line 160) and is actively used by `GetProducts` (line 30), `SearchProducts` (line 93), and `GetProductsByCategory` (line 70) — all optimized in experiments 14 and 15. But `GetProduct` (single by ID) was never connected to the cache.

## Theory

`FindAsync(id)` performs two operations: (1) checks the EF change tracker for a tracked entity with that key, (2) if not found, issues a `SELECT TOP 1 ... WHERE Id = @p0` database query. Since the change tracker is empty for most requests (read-only endpoints), it always hits the database. The returned entity is then change-tracked, adding allocation overhead for an entity that is immediately serialized to JSON and discarded.

With the products cache warm (refreshed every 30s by the heavily-called list/search/category endpoints), the single-product lookup can be served from the in-memory list in microseconds instead of milliseconds. The cache is proven reliable — experiments 14 and 15 used the same cache for list, search, and category endpoints and both improved.

## Proposed Fixes

1. **Check the existing cache before `FindAsync`:** Before the database call, check if `_cachedProducts` is non-null and `DateTime.UtcNow < _cacheExpiry`. If so, search the cached list for the product by ID using `FirstOrDefault(p => p.Id == id)`. Only fall back to `FindAsync` if the cache is cold or the product isn't in the cache (handles newly-created products not yet cached).

## Expected Impact

- p95 latency: ~6ms reduction per request from eliminating 1 DB round trip and change tracking overhead
- Connection pool: 1 fewer connection checkout per GetProduct request
- High confidence: identical pattern already proven in experiments 14 (cache for GetProducts) and 15 (cache for Search/Category)
