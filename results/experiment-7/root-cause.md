# Root Cause Analysis — Experiment 7

> Generated: 2026-03-15 15:29:44 | Classification: narrow — The LoadCart method loads all CartItems into memory (line 109) then filters in C# (line 110) and does per-item Product lookups in a loop (line 117); replacing this with a server-side .Where() filter and .Include(ci => ci.Product) or a join is a single-file query optimization that does not change any API contract, dependency, or schema.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 685.029124999999ms | 7546.103045ms |
| Requests/sec | 884.3 | 125.5 |
| Error Rate | 0% | 0% |

---
# Eliminate full CartItems table scan and N+1 product lookups in Cart page LoadCart

> **File:** `SampleApi/Pages/Cart/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Cart/Index.cshtml.cs:109`, `LoadCart()` pulls **every cart item in the database** into memory:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

Then at lines 115-130, it executes an **N+1 query** — one `FindAsync` per cart item to fetch product details:

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
    ...
}
```

Additionally, `OnPostClearAsync` at lines 91-98 has the same full-table scan AND per-item `SaveChangesAsync` in a loop:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

The CPU profile shows SQL Server engine at 13.5% of samples and EF Core change-tracking overhead (NavigationFixer, SortedDictionary enumeration) at ~2% — full table scans with tracking are a direct contributor. The GC report shows 800 MB/sec allocation and 3.4GB peak heap; materializing the entire CartItems table into tracked entities causes massive LOH allocations.

## Theory

The CartItems table grows rapidly under load — each of the 500 VUs adds items every iteration, so the table can contain thousands of rows during peak load. Loading all of them into memory on every GET /Cart request creates three compounding problems:

1. **SQL overhead**: Full table scan transfers all rows over the TDS connection, visible in the CPU profile's TDS parsing and Unicode decoding hotspots.
2. **GC pressure**: Materializing thousands of tracked entities with change tracking creates large object graphs that trigger Gen2 collections (33 Gen2 collections observed vs only 10 Gen0 — an inverted, unhealthy pattern).
3. **N+1 round trips**: Each `FindAsync` in the loop is a separate DB round trip, adding latency linearly with cart size.

## Proposed Fixes

1. **Server-side filter + batch product lookup in LoadCart**: Replace `_context.CartItems.ToListAsync()` at line 109 with `_context.CartItems.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync()`. Replace the N+1 product loop (lines 115-130) with a batch query: collect productIds, use `_context.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`, then loop over the dictionary.

2. **Fix OnPostClearAsync**: Replace lines 91-98 with a server-side filtered query and use `RemoveRange` + single `SaveChangesAsync` instead of per-item saves.

## Expected Impact

- p95 latency: ~100-150ms reduction for Cart page requests by eliminating the full scan and N+1 pattern
- SQL Server CPU: Significant reduction in TDS parsing and query execution overhead
- GC pressure: Reduced heap allocation from materializing only session-specific items with AsNoTracking
- Overall p95 improvement: ~2-3%, accounting for both direct latency reduction and reduced SQL/GC contention across all endpoints

