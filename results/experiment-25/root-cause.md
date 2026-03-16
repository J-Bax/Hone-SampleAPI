# Root Cause Analysis — Experiment 25

> Generated: 2026-03-16 00:09:15 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 546.113655ms | 7546.103045ms |
| Requests/sec | 1100.1 | 125.5 |
| Error Rate | 0% | 0% |

---
# Replace NEWID() random ordering with efficient deterministic query for featured products

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:29`, the home page fetches featured products using:

```csharp
FeaturedProducts = await _context.Products.AsNoTracking()
    .OrderBy(_ => EF.Functions.Random())
    .Take(12)
    .ToListAsync();
```

This translates to SQL: `SELECT TOP 12 * FROM Products ORDER BY NEWID()`. The `NEWID()` function generates a GUID for **every row** in the table (1,000 products), then SQL Server must sort all 1,000 rows to select the top 12. This is a guaranteed full table scan plus an expensive sort operation.

The CPU profile shows SQL Server engine modules consuming **17% of total samples** (91,700 samples in sqlmin/sqllang/sqldk). The home page query is one of the contributors because `ORDER BY NEWID()` cannot use any index and forces a full scan + sort on every request.

Additionally, at line 32, a separate `CountAsync()` query runs:

```csharp
TotalProducts = await _context.Products.CountAsync();
```

This is a second round trip that also touches the Products table.

## Theory

Under high concurrency (up to 500 VUs), every VU iteration hits the home page, triggering the `ORDER BY NEWID()` query. With 1,000 rows, SQL Server must:
1. Scan the entire clustered index
2. Compute NEWID() per row (GUID generation)
3. Sort all 1,000 rows by the generated GUIDs
4. Return only the top 12

This creates significant SQL Server CPU pressure and lock contention under load. The sort operation also allocates tempdb workspace. Replacing with a deterministic ordering on an indexed column (e.g., `Id` descending) allows SQL Server to use the clustered index directly and return the first 12 rows without scanning or sorting the entire table.

## Proposed Fixes

1. **Replace Random() with deterministic indexed ordering:** At line 29, change `OrderBy(_ => EF.Functions.Random())` to `OrderByDescending(p => p.Id)` (or `OrderByDescending(p => p.CreatedAt)` for "newest" semantics). The `Id` column is the clustered index primary key, so `ORDER BY Id DESC` is a simple backward index scan returning exactly 12 rows with zero sort overhead.

2. **Optionally combine with Count:** The `CountAsync()` at line 32 could be replaced with a known-count approach or combined into the same query, but this is a minor secondary optimization.

## Expected Impact

- Eliminates per-request full table scan + sort for the home page query
- Reduces SQL Server CPU time for this endpoint by ~80-90%
- Expected per-request latency reduction of ~25-40 ms for the home page
- Overall p95 improvement: ~0.3-0.4% (home page is ~5.5% of traffic)

