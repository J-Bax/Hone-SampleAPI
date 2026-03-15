# Root Cause Analysis — Experiment 7

> Generated: 2026-03-15 06:43:15 | Classification: narrow — The optimization involves replacing full table scans (ToListAsync followed by in-memory LINQ) with server-side queries (Take, OrderByDescending, Count) entirely within the single OnGetAsync method of Index.cshtml.cs, requiring no dependency, schema, or API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 583.09564ms | 1596.242785ms |
| Requests/sec | 1075.8 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# Full table scans of Products and Reviews on home page

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28-29`, the home page loads every product in the database:

```csharp
var allProducts = await _context.Products.ToListAsync();
FeaturedProducts = allProducts.OrderBy(_ => Guid.NewGuid()).Take(12).ToList();
```

At lines 36-37, it also loads every review:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
RecentReviews = allReviews.OrderByDescending(r => r.CreatedAt).Take(5).ToList();
```

Neither query uses `AsNoTracking()`. The seed data contains 1,000 products and ~2,000 reviews, so each home page hit materializes ~3,000 entities with full EF Core change tracking — only to discard all but 17 of them (12 featured + 5 recent).

## Theory

The home page is hit once per k6 VU iteration (~5.6% of all requests). Under 500 VUs, this means hundreds of concurrent requests each materializing ~3,000 tracked entities. The CPU profiler shows 42% inclusive time in `SingleQueryingEnumerable.MoveNextAsync` (EF materialization) and ~14K samples in change-tracking overhead (`NavigationFixer`, `StateManager`, `InternalEntityEntry`). The memory profiler confirms a 1,273 MB/sec allocation rate with an inverted GC generation profile (Gen2 >> Gen0), and max GC pauses of 334ms — nearly the entire p95 budget. Each home page request allocates megabytes of tracked entity graphs that are immediately discarded, directly driving the Gen2 collection pressure and GC pause spikes.

The `OrderBy(_ => Guid.NewGuid())` at line 29 also forces a full in-memory sort of 1,000 items per request, adding CPU overhead for the random selection.

## Proposed Fixes

1. **Server-side random sampling + AsNoTracking:** Replace the full `Products.ToListAsync()` with a server-side query: use `AsNoTracking().OrderBy(_ => EF.Functions.Random()).Take(12).ToListAsync()` (or SQL Server's `NEWID()` via raw SQL) to fetch only 12 products. Replace `TotalProducts = allProducts.Count` with `await _context.Products.CountAsync()`.

2. **Server-side Top-N reviews + AsNoTracking:** Replace `Reviews.ToListAsync()` with `_context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync()` to fetch only the 5 most recent reviews directly from SQL.

## Expected Impact

- p95 latency: Per-request latency for the home page should drop by ~40-60ms as entity materialization goes from ~3,000 to ~27 entities (99% reduction) and change tracking is eliminated.
- GC pressure: Allocation volume from this endpoint drops by ~99%, significantly reducing Gen2 collection frequency and max pause times.
- Overall p95 improvement: ~5% (5.6% traffic share × ~50ms reduction / 583ms current p95).

