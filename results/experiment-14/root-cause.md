# Root Cause Analysis — Experiment 14

> Generated: 2026-03-14 18:48:36 | Classification: narrow — Adding in-memory caching of products in the GetProducts method (lines 23-27) is a single-file implementation change with no API contract, dependency, migration, or test changes required.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 473.982485ms | 2054.749925ms |
| Requests/sec | 1325.1 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# GetProducts re-queries all 1000 products from the database on every request

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:24-27`, `GetProducts` materializes all 1000 products from the database on every call:

```csharp
// ProductsController.cs:24-27
[HttpGet]
public async Task<ActionResult<IEnumerable<Product>>> GetProducts()
{
    var products = await _context.Products.ToListAsync();
    return Ok(products);
}
```

The Products table is seeded once at startup (`SeedData.cs:37-49`, 1000 products) and **never modified** during k6 load tests — no k6 request writes to the Products table. Yet every VU iteration triggers a full table scan of all 1000 products with entity change tracking.

The CPU profiler shows `TdsParserStateObject.TryReadChar` as the top exclusive-time method (1.72%) and `TryReadPlpUnicodeCharsChunk` at 0.33%, both driven by reading large nvarchar columns (Product.Description) from SQL. The GC profiler reports 730 MB/sec allocation rate — each GetProducts call creates 1000 tracked Product entities plus identity-map entries and snapshot copies, contributing significantly to the Gen0→Gen1 mid-life promotion storm (131 Gen0 vs 123 Gen1 collections).

## Theory

At ~73.6 calls/sec (5.56% of 1325 RPS), GetProducts materializes ~73,600 Product entities per second — all identical data that hasn't changed since startup. Each entity involves:
- SQL round-trip reading 1000 rows with Description nvarchar columns (~150 bytes each)
- EF Core identity-map lookup and tracking-entry creation per entity
- Original-values snapshot allocation per entity (~200 bytes overhead)
- JSON serialization of the full 1000-element array

This produces ~30-40 MB/sec of allocations from this single endpoint alone. The entities survive Gen0 (because the request hasn't completed when Gen0 runs), get promoted to Gen1, then die — the classic mid-life crisis pattern that causes expensive Gen0 pauses (up to 119ms observed).

Since the underlying data never changes during load tests, every one of these queries returns the exact same result.

## Proposed Fixes

1. **In-memory cache with time-based expiration**: Add a static cached result field to the controller. On the first request (or after expiry), query the database once and store the result. Subsequent requests return the cached list directly, bypassing the database entirely. A 30-second TTL is more than sufficient since products don't change during load tests. The JSON response is identical to the current implementation.

   The cache should populate using a non-tracked query (projection to anonymous types or a detached list) so the cached objects don't hold references to a disposed DbContext.

## Expected Impact

- **p95 latency**: ~15-20ms direct reduction per GetProducts request (eliminates SQL round-trip + materialization)
- **RPS**: Modest improvement from freeing ~73.6 DB connections/sec for other endpoints
- **GC pressure**: Eliminates ~30-40 MB/sec of entity allocations, reducing Gen0 collection frequency and max pause duration
- The indirect benefit of reduced DB load (fewer connections, less lock contention, less I/O) improves latency for ALL endpoints, amplifying the overall impact beyond the direct 5.56% traffic share

