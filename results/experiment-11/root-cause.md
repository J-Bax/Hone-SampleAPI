# Root Cause Analysis — Experiment 11

> Generated: 2026-03-10 03:34:27 | Classification: narrow — Modifies only query logic in OnGetAsync method body to replace EF.Functions.Random() with more efficient sampling; no dependencies, APIs, or database schema changes needed.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 408.6216ms | 888.549155000001ms |
| Requests/sec | 1345.6 | 683.2 |
| Error Rate | 0% | 0% |

---
# Replace ORDER BY NEWID() with efficient random product sampling

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28`:

```csharp
FeaturedProducts = await _context.Products.AsNoTracking()
    .OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync();
```

This translates to SQL: `SELECT TOP(12) ... FROM Products ORDER BY NEWID()`. SQL Server must:
1. Full-scan all 1,000 product rows (confirmed by `SeedData.cs:37`: 1,000 products seeded)
2. Generate a GUID per row
3. Sort all 1,000 rows by the generated GUIDs
4. Return the top 12

The home page is hit every VU iteration (`baseline.js:110`: `http.get(BASE_URL + '/')`), so at 500 VUs this O(n log n) sort runs hundreds of times per second. The CPU profile confirms SQL Server engine processing at ~15.6% of total CPU, with EF Core materialization at 22% inclusive — this `ORDER BY NEWID()` contributes to both hotspot categories.

Additionally, the page makes 4 sequential DB round trips (lines 28–35):
1. `OrderBy(EF.Functions.Random()).Take(12)` — featured products (expensive sort)
2. `CountAsync()` — total product count
3. `Categories.AsNoTracking().ToListAsync()` — all 10 categories
4. `Reviews.AsNoTracking().OrderByDescending(...).Take(5)` — recent reviews

## Theory

`ORDER BY NEWID()` forces a full clustered-index scan and sort on every single home-page request. For 1,000 products, SQL Server generates 1,000 GUIDs, performs a full sort (O(n log n)), then discards 988 rows — extremely wasteful. Under the high-concurrency load test (up to 500 VUs), this query runs concurrently hundreds of times, creating heavy contention on the Products table's data pages and consuming significant SQL Server CPU for sorting.

The memory-gc report shows 610 MB/sec allocation rate — materializing sort buffers for 1,000 rows only to discard most contributes to this churn. The 46.9ms max Gen0 pause correlates with these allocation spikes.

## Proposed Fixes

1. **Count-then-skip random sampling:** First obtain the product count (already fetched at line 29 for `TotalProducts`), then generate 12 random offsets within `[0, count)` and fetch products by those offsets using `Skip(offset).Take(1)` — or better, generate a random start point and use `OrderBy(p => p.Id).Skip(randomStart).Take(12)` for a single indexed seek+scan that returns 12 consecutive products. Move the `CountAsync` (line 29) before the featured query so the count can seed the random offset.

2. **Max-ID random range:** Query `MAX(Id)` (fast index seek), generate a random ID in `[1, maxId]`, then `Where(p => p.Id >= randomId).OrderBy(p => p.Id).Take(12)`. This uses the clustered PK index with a single seek — O(1) vs O(n log n). Handle wrap-around if fewer than 12 products remain above the random ID.

## Expected Impact

- p95 latency: ~5–10% reduction from eliminating the full table sort on every home page load
- SQL Server CPU: measurable reduction from removing O(n log n) sort per request
- Allocation rate: modest reduction from not materializing 1,000-row sort buffers
- The improvement compounds under high concurrency since the current sort holds shared locks during the full scan

