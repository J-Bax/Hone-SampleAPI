# Root Cause Analysis — Experiment 11

> Generated: 2026-03-14 16:46:38 | Classification: narrow — Adding .AsNoTracking() to lines 35 and 63 (read-only queries) is a single-file EF Core optimization that doesn't require dependencies, migrations, API changes, or test modifications.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 496.6681ms | 2054.749925ms |
| Requests/sec | 1295.4 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Products page tracks 1000 entities needlessly on read-only render

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:35`, the `OnGetAsync` method loads all 1000 products with full EF Core change tracking:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

The categories query on line 63 is also tracked:

```csharp
Categories = await _context.Categories.ToListAsync();
```

This page is entirely read-only — it never calls `SaveChangesAsync()`. Yet every request creates 1000+ `InternalEntityEntry` objects, runs `NavigationFixer.InitialFixup`, and populates `SortedDictionary`/`SortedSet` data structures for relationship fixup.

The CPU profiling confirms this pattern:
- `StateManager.StartTrackingFromQuery`: 2.3% inclusive CPU (679 `InternalEntityEntry` construction samples + 536 `NavigationFixer.InitialFixup` samples)
- `SortedDictionary` enumeration: 1.11% exclusive (~8,500 samples across MoveNext, GetEnumerator, constructor)

The GC profiling shows a near-1:1 Gen1:Gen0 ratio (119/127 = 0.94), meaning almost every Gen0 collection promotes survivors. With ~794 MB/sec allocation rate (~612 KB per request), request-scoped tracked entity graphs are the most likely mid-lived objects causing this promotion pressure. The Products page with 1000 tracked entities is the single largest contributor per request.

## Theory

EF Core change tracking creates an `InternalEntityEntry` per materialized entity plus snapshot data and `SortedDictionary`/`SortedSet` structures for navigation relationship fixup. With 1000 Product entities per request, these allocations are substantial (~400+ bytes per entity in tracking overhead). These objects are request-scoped: they survive the Gen0 collection window (they live for the duration of the request pipeline) but die before Gen2, creating exactly the mid-lived promotion pattern identified in the GC profile.

At ~72 requests/sec to this endpoint under 500 VUs, that's ~72,000 unnecessarily tracked entities per second generating allocation churn that drives Gen1 collections. The 89.4ms max Gen1 pause directly spikes p95 latency for any request that coincides with collection.

## Proposed Fixes

1. **Add `AsNoTracking()` to both queries in `OnGetAsync`:** Change line 35 to `await _context.Products.AsNoTracking().ToListAsync()` and line 63 to `await _context.Categories.AsNoTracking().ToListAsync()`. This eliminates all `InternalEntityEntry` construction, `NavigationFixer.InitialFixup`, and `SortedDictionary` overhead for this page. No behavioral change since the page never writes.

## Expected Impact

- p95 latency: ~20-30ms reduction per request through eliminated tracking overhead and reduced Gen1 GC pause frequency
- Memory: ~35-40% reduction in per-request allocation for this endpoint (tracking structures for 1000+ entities)
- Overall: ~2-3% p95 improvement accounting for 5.5% traffic share and cascading GC pressure reduction

