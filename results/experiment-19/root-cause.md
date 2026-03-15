# Root Cause Analysis — Experiment 19

> Generated: 2026-03-14 21:04:00 | Classification: narrow — Caching featured products can be implemented entirely within Index.cshtml.cs by injecting IMemoryCache and adding cache logic to OnGetAsync(), requiring no additional files, packages, migrations, or API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 407.72407ms | 2054.749925ms |
| Requests/sec | 1693.7 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Cache featured products to avoid ORDER BY NEWID() on every request

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28`, the home page selects 12 random featured products on every request:

```csharp
FeaturedProducts = await _context.Products.OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync();
```

`EF.Functions.Random()` translates to `ORDER BY NEWID()` in SQL Server, which generates a random GUID for each of the 1000 product rows and sorts them before returning the top 12. This is followed by three additional tracked queries at lines 29-35:

```csharp
TotalProducts = await _context.Products.CountAsync();          // round trip 2
Categories = await _context.Categories.ToListAsync();           // round trip 3 (tracked)
RecentReviews = await _context.Reviews.OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync(); // round trip 4 (tracked)
```

That is 4 sequential DB round trips per home page request, with the first being the most expensive due to the full-table random sort. The Categories and Reviews queries also use change tracking unnecessarily for read-only rendering.

## Theory

`ORDER BY NEWID()` forces SQL Server to: (1) scan all 1000 product rows, (2) compute a GUID per row, (3) sort all 1000 by GUID, (4) return the top 12. Under 500 VUs, hundreds of these expensive sorts compete for SQL Server CPU and buffer pool resources per second. Categories (10 rows, never changes) and the "top 5 recent reviews" also re-query the DB on every request. The tracked entities from Categories and Reviews queries add change-tracker overhead and allocation pressure for data that is only rendered, never modified.

## Proposed Fixes

1. **Cache featured products** with a static `List<Product>` and a short TTL (e.g., 15-30 seconds), using the same double-check-lock pattern as `ProductsController`. This eliminates the `ORDER BY NEWID()` sort from the hot path while still rotating featured products periodically.
2. **Cache categories** separately (they never change during the test). Add `AsNoTracking()` to the Reviews query at line 35 to eliminate tracking overhead.

## Expected Impact

- p95 latency: ~40ms reduction for home page requests (eliminating NEWID() sort + reducing round trips)
- SQL Server CPU: reduced contention from eliminating the per-request full-table random sort
- Overall p95 improvement: ~0.55%

