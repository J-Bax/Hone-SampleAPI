# Root Cause Analysis — Experiment 18

> Generated: 2026-03-15 21:40:10 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 535.98694ms | 7546.103045ms |
| Requests/sec | 1052.1 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add pagination and DTO projection to product list endpoints

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:25`, the `GetProducts` endpoint materializes the entire Products table (1000 rows with seed data):

```csharp
var products = await _context.Products.AsNoTracking().ToListAsync();
```

At `ProductsController.cs:72-76`, the `SearchProducts` endpoint with `q=Product` matches all 1000 products because every seeded product name starts with "Product" (see `SeedData.cs:42`: `Name = $"Product {i:D4} - {category}"`):

```csharp
var results = await _context.Products
    .AsNoTracking()
    .Where(p => EF.Functions.Like(p.Name, $"%{q}%") ||
                EF.Functions.Like(p.Description, $"%{q}%"))
    .ToListAsync();
```

When `q` is empty (line 80), the fallback also returns all products:

```csharp
var allProducts = await _context.Products.AsNoTracking().ToListAsync();
```

Each Product entity includes a ~120-character Description string (`SeedData.cs:43-44`). With 1000 products, each response is approximately 300–400KB of JSON.

The CPU profiler confirms this pattern is the dominant bottleneck:
- `SingleQueryingEnumerable.MoveNextAsync`: 8.5% inclusive — massive row iteration volume
- `TdsParserStateObject.TryReadChar`: 2.18% — reading large volumes of character data from SQL
- `StringConverter.Write`: 1.07% — JSON serialization of many string properties
- `UnicodeEncoding.GetCharCount/GetChars`: 0.9% — decoding nvarchar data proportional to fetched string volume

The memory profiler reports 49GB total allocations with 85% Gen0→Gen1 promotion, and GC pauses up to 67.9ms directly contributing to p95 tail latency.

## Theory

These two endpoints account for ~11.1% of total k6 traffic (each called once per iteration out of 18 requests). Every call materializes 1000 Product entity objects with all 7 columns, creating:

1. **SQL overhead**: TDS parser reads ~200KB+ of character data per query (Name + Description + Category × 1000 rows)
2. **Allocation pressure**: 1000 Product objects + internal EF buffers + LINQ intermediate collections per call — hundreds of KB of mid-lived allocations that survive Gen0 and trigger Gen1 collections
3. **JSON serialization overhead**: System.Text.Json must serialize 1000 objects with string properties, dominating the StringConverter.Write hotspot
4. **Response transmission**: 300–400KB JSON responses consume Kestrel write buffers and increase time-to-last-byte under concurrent load

Under 500 VUs, approximately 58 concurrent requests per second hit these two endpoints, each reading 1000 rows — that is 58,000 product rows materialized per second just from list/search. This is the single largest contributor to the SQL read and JSON serialization hotspots in the CPU profile.

## Proposed Fixes

1. **Add pagination with default limit**: Introduce optional `page` (default 1) and `pageSize` (default 50) query parameters to both `GetProducts` and `SearchProducts`. Use `.Skip((page-1)*pageSize).Take(pageSize)` to return a bounded result set. Without parameters, the endpoint returns the first 50 products (a sensible default). Consumers needing more can paginate or request a larger page.

2. **Add Select projection excluding Description for list responses**: For `GetProducts` (line 25), `SearchProducts` (lines 72–80), and `GetProductsByCategory` (lines 56–59), add `.Select()` to project only the columns needed for listing (Id, Name, Price, Category, CreatedAt, UpdatedAt) and exclude the heavy Description field. Return anonymous objects or a lightweight DTO. The single-item `GetProduct/{id}` endpoint (line 35) continues to return the full entity with Description.

## Expected Impact

- **p95 latency**: Estimated ~15–30ms reduction per request for GetProducts and SearchProducts (from ~50ms to ~5ms per-request SQL+serialization cost). GetProductsByCategory would see ~3–5ms from projection alone.
- **Allocations**: ~90% reduction in Product object allocations from these endpoints, reducing Gen1 GC collections and associated pause spikes (currently up to 67.9ms)
- **Overall p95**: ~1.5–3% improvement accounting for both direct latency reduction and indirect GC pressure relief. At 536ms current p95, this translates to ~8–16ms overall reduction.

