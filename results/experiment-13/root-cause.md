# Root Cause Analysis — Experiment 13

> Generated: 2026-03-15 18:35:19 | Classification: narrow — Adding .AsNoTracking() to the read-only queries in GetCart (lines 25-27 and 30-32) is a single-file change to method body internals that does not alter dependencies, API contracts, or require any other file modifications.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 546.34976ms | 7546.103045ms |
| Requests/sec | 1028.9 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add AsNoTracking to read-only cart and product queries

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-32`, `GetCart` executes two queries without `AsNoTracking()`:

```csharp
var sessionItems = await _context.CartItems
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();                              // Line 25-27: tracking enabled

var products = await _context.Products
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);               // Line 30-32: tracking enabled
```

Both result sets are used purely for reading — building a response DTO at lines 43-53. Neither entity is modified.

The memory-gc profiler reports:
- 48.3GB total allocations (~402 MB/sec)
- 86.5% Gen1 promotion rate — "characteristic of EF Core DbContext tracking"
- Peak heap of 1.05GB

The profiler explicitly identifies EF Core change tracking as the likely top allocator: "Use AsNoTracking() for read-only queries to eliminate change tracker allocations."

## Theory

When EF Core materializes entities with change tracking enabled, it creates a `StateEntry` per entity, snapshot copies of all property values, and identity map entries. These objects survive Gen0 (they're referenced by the DbContext for the request lifetime), promoting to Gen1 — explaining the abnormal 86.5% promotion rate. At 1006 RPS, GetCart materializes cart items + product entities with full tracking on every call. The product entities are particularly wasteful — loaded with all columns (Name, Description, Price, Category, timestamps) just to read Name and Price.

The cumulative allocation volume drives 199 GC collections during the test, creating throughput drag even with healthy individual pause times.

## Proposed Fixes

1. **Add `.AsNoTracking()` to both queries in GetCart (lines 25 and 30):**
   ```csharp
   var sessionItems = await _context.CartItems
       .AsNoTracking()
       .Where(c => c.SessionId == sessionId)
       .ToListAsync();
   ```
   Same for the Products query at line 30.

2. **Add `.AsNoTracking()` to the ClearCart CartItems query (line 145):** Although items are removed, EF Core's `RemoveRange` works with untracked entities if you attach them. Alternatively, keep tracking here but ensure GetCart (the hot read path) is optimized.

## Expected Impact

- Allocation reduction: eliminates ~2 StateEntry objects + snapshots per cart item and product per request
- GC pressure: reduced Gen1 promotion rate, fewer collections
- p95 latency: ~2-4ms reduction from reduced allocation/GC overhead
- The memory profiler's 48.3GB volume would decrease measurably on this high-frequency path

