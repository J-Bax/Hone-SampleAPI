# Root Cause Analysis — Experiment 20

> Generated: 2026-03-14 21:48:05 | Classification: narrow — The proposed change adds static caching variables for categories and avoids redundant CountAsync on line 54 by reusing the already-loaded Categories list, all contained within a single PageModel class with no dependency additions, API changes, or migration files required.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 390.930625ms | 2054.749925ms |
| Requests/sec | 1759.2 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Cache categories and eliminate redundant CountAsync on home page

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:50`, the home page executes a `CountAsync` query on every request:

```csharp
TotalProducts = await _context.Products.CountAsync();
```

This is redundant because the ProductsController already maintains a warm in-memory cache of all 1,000 products (refreshed every 30s). The count can be derived from the existing featured products query or a cached value.

At line 53, categories are loaded from the database on every request without caching or `AsNoTracking`:

```csharp
Categories = await _context.Categories.ToListAsync();
```

Categories are static reference data — 10 rows seeded once at startup (`SeedData.cs:24-28`), never modified by the k6 scenario. Yet every home page request allocates a tracked EF entity set for them.

Combined, the home page fires 3–4 DB queries per request (featured products when cache expired, `CountAsync`, `Categories`, and `RecentReviews`). Two of these (`CountAsync` and `Categories`) are entirely avoidable.

## Theory

With 500 VUs, each home page request opens 2 unnecessary DB connections from the default 100-connection SQL Server pool. At ~98 iterations/sec across all VUs, that's ~196 extra connection checkouts per second competing with every other endpoint's queries. The runtime counters confirm the server is I/O-bound (CPU avg 15%) — it spends most time waiting on database round trips. Every saved round trip reduces contention systemically, benefiting all endpoints' tail latency.

Additionally, `Categories.ToListAsync()` without `AsNoTracking()` engages EF Core's change tracker for 10 entities that are never modified, adding allocation pressure (the memory-gc report notes 395 MB/sec allocation rate and 79% Gen0→Gen1 survival — per-request tracked entities contribute to this).

## Proposed Fixes

1. **Cache categories with a static field and TTL:** Add a `_cachedCategories` list with a 60-second TTL, identical to the `_cachedFeaturedProducts` pattern already at lines 15–17. On cache hit, return the cached list and derive `TotalCategories` from `_cachedCategories.Count`. On cache miss, load with `AsNoTracking()` and populate the cache.

2. **Replace `CountAsync` with a cached product count:** Add a static `_cachedProductCount` field updated alongside the featured products cache refresh (line 38). When the cache is warm, use the cached count instead of issuing a separate `CountAsync` query.

## Expected Impact

- p95 latency: ~10ms reduction per home page request from eliminating 2 DB round trips
- Systemic benefit: reduced connection pool contention improves tail latency across all concurrent requests
- GC: fewer tracked entities reduces allocation rate slightly

