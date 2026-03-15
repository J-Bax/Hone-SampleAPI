# Cache product list to eliminate per-request full-table DB load

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Products/Index.cshtml.cs:35`, every Products page request loads all 1000 products from the database:

```csharp
var allProducts = await _context.Products.AsNoTracking().ToListAsync();
```

This materializes 1000 `Product` entities (each with Name, Description, Category, Price, timestamps) on every request, then filters and paginates in memory at lines 38-60. Meanwhile, `ProductsController.cs:14-17` already maintains a static product cache with a 30-second TTL:

```csharp
private static List<Product>? _cachedProducts;
private static DateTime _cacheExpiry = DateTime.MinValue;
private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
```

The Products page does not use this cache and re-queries the DB independently, generating redundant SQL traffic and allocation pressure.

The memory profiler reports **402 MB/sec allocation rate** and **238ms max GC pause** — each Products page request allocates ~1000 Product objects plus backing strings, contributing to the Gen0/Gen1 collection pressure.

## Theory

With 500 VUs and the Products page representing ~5.6% of traffic, the DB receives dozens of concurrent `SELECT * FROM Products` queries per second just from this page. Each query returns all 1000 rows, transfers them over TDS, and materializes 1000 entity objects (even with AsNoTracking, the objects and their string properties are allocated). This drives both SQL Server CPU (confirmed by the 12.1% `sqlmin` hotspot) and managed heap allocation pressure (contributing to the 47GB total allocation volume). Since product data is static during the test, every query after the first is pure waste.

## Proposed Fixes

1. **Add a static product cache** to `Products/Index.cshtml.cs` using the same double-check-lock pattern as `ProductsController.GetOrPopulateCacheAsync()`: a `static List<Product>?` with a `SemaphoreSlim` gate and 30-second TTL. Replace the `ToListAsync()` call at line 35 with a cache lookup.

## Expected Impact

- p95 latency: ~35ms reduction for Products page requests (eliminating DB round trip and 1000-entity materialization)
- Allocation reduction: ~1000 fewer Product allocations per cached request, reducing GC pressure
- Overall p95 improvement: ~0.5%
