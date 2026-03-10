# Root Cause Analysis — Experiment 16

> Generated: 2026-03-10 05:15:38 | Classification: narrow — The optimization modifies only the query logic in OnGetAsync() method within a single file, replacing EF.Functions.Random() with a more efficient random sampling algorithm—this is a pure implementation change with no dependency, migration, schema, or API contract modifications required.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 407.84418ms | 888.549155000001ms |
| Requests/sec | 1359.9 | 683.2 |
| Error Rate | 0% | 0% |

---
# Replace ORDER BY NEWID() with efficient random sampling

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs` line 28:
```csharp
FeaturedProducts = await _context.Products.AsNoTracking()
    .OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync();
```

`EF.Functions.Random()` translates to `ORDER BY NEWID()` in SQL Server. This forces SQL Server to:
1. Scan all 1,000 product rows
2. Compute a GUID for each row
3. Sort all 1,000 rows by the random GUID
4. Return the top 12

The home page is hit once per VU iteration (baseline.js line 110). At 500 VUs firing back-to-back, this generates hundreds of full-table-scan-plus-sort operations per second.

Experiment 11 attempted this fix but went stale (branch conflict), so it was never applied.

## Theory

`ORDER BY NEWID()` is an O(N log N) operation where N is the total product count (1,000). SQL Server cannot use any index for this sort since NEWID() is non-deterministic. Every execution requires a full table scan to read all rows (including the nvarchar(max) Description column) followed by an in-memory sort. Under 500 concurrent VUs, this creates massive contention on the Products table and contributes to the SQL data reading overhead (10.2% in TdsParserStateObject.TryReadChar). The sort also consumes tempdb resources under high concurrency.

## Proposed Fixes

1. **Random offset sampling:** Generate a random offset in C# using `Random.Shared.Next(0, totalCount - 12)`, then use `.Skip(offset).Take(12)`. This translates to an efficient `OFFSET/FETCH` query that reads only 12 rows via index seek. Requires a `CountAsync()` first, but that's a lightweight aggregate.

2. **Random ID range sampling:** Generate a random ID range and query `WHERE Id >= randomStart ORDER BY Id TAKE 12`. Since Product IDs are sequential (seeded 1-1000), this avoids both the full scan and the sort.

## Expected Impact

- p95 latency: ~5-10% reduction. The home page is 1 of 13 requests per iteration; eliminating the full table scan + sort removes a significant per-request SQL cost.
- RPS: ~5-8% increase from reduced SQL Server contention and tempdb pressure.
- CPU: reduced SQL data reading overhead — no longer reading all 1,000 product Description fields just to pick 12.

