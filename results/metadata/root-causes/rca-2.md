# Replace ORDER BY NEWID() with efficient Skip/Take random sampling

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28-32`, the home page loads featured products using:

```csharp
FeaturedProducts = await _context.Products.AsNoTracking()
    .OrderBy(_ => EF.Functions.Random())
    .Take(12)
    .Select(p => new Product { Id = p.Id, Name = p.Name, ... })
    .ToListAsync();
```

This translates to `SELECT TOP 12 ... FROM Products ORDER BY NEWID()` in SQL Server. On the very next line (33), the total count is already queried:

```csharp
TotalProducts = await _context.Products.CountAsync();
```

Experiment 25 attempted a "deterministic" replacement but was stale — likely because the deterministic approach still involved a sort or the random distribution was too predictable.

## Theory

`ORDER BY NEWID()` is one of the most expensive random-sampling patterns in SQL Server. For each of the 1000 rows in Products, SQL Server must:
1. Generate a GUID via `NEWID()`
2. Sort all 1000 rows by the generated GUIDs (O(n log n))
3. Return only the top 12

Under 500 concurrent VUs, this creates extreme CPU contention: 500 simultaneous full-table sorts. The sort operation cannot be indexed away because the sort key is computed at runtime.

A Skip/Take approach using `Random.Shared.Next(count - 12)` as the skip offset avoids sorting entirely. SQL Server executes a simple `OFFSET @skip ROWS FETCH NEXT 12 ROWS ONLY` on the clustered index, which is O(skip + 12) — far cheaper than sorting all 1000 rows. The results are still pseudo-random across requests.

## Proposed Fixes

1. **Replace `OrderBy(EF.Functions.Random())` with Skip/Take:** First query the count (or reorder to use `TotalProducts` which is already queried), then compute a random offset: `var offset = Random.Shared.Next(Math.Max(1, totalProducts - 12));`. Replace the OrderBy/Take chain with `.OrderBy(p => p.Id).Skip(offset).Take(12)`. This gives different products on each page load without the expensive sort. Move the `CountAsync` query before the featured products query so `TotalProducts` is available.

## Expected Impact

- p95 latency: estimated ~15-25ms reduction per home page request (eliminates full-table sort)
- The DB CPU savings cascade: freed CPU serves other concurrent queries faster
- Overall p95 improvement: ~2% (5.6% traffic share × ~20ms / ~544ms current p95)
