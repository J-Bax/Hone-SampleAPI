# Root Cause Analysis — Experiment 1

> Generated: 2026-03-22 18:31:50 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 15107.2967ms | 15107.2967ms |
| Requests/sec | 96.7 | 96.7 |
| Error Rate | 3.47% | 3.47% |

---
# SearchProducts LIKE query returns unbounded results matching all 1000 products

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:93-127`, the search endpoint uses a LIKE query with no result limit:

```csharp
[HttpGet("search")]
public async Task<ActionResult<IEnumerable<Product>>> SearchProducts([FromQuery] string? q)
{
    if (!string.IsNullOrWhiteSpace(q))
    {
        var results = await _context.Products
            .AsNoTracking()
            .Where(p => EF.Functions.Like(p.Name, $"%{q}%") ||
                        EF.Functions.Like(p.Description, $"%{q}%"))
            .Select(p => new Product { ... })
            .ToListAsync();
        return Ok(results);
    }
    // Falls through to return ALL products if q is empty
    var allProducts = await _context.Products.AsNoTracking()...ToListAsync();
    return Ok(allProducts);
}
```

The k6 scenario (`baseline.js:59`) always searches for `"Product"`:

```javascript
const searchRes = http.get(`${BASE_URL}/api/products/search?q=Product`);
```

Since all 1000 products are named `"Product XXXX - Category"` (see `SeedData.cs:42`), this LIKE query matches and returns ALL 1000 rows every time.

## Theory

This endpoint has two performance problems:

1. **Full table scan**: `LIKE '%Product%'` with a leading wildcard prevents SQL Server from using any index on the `Name` column. The query must scan every row in the Products table and also scan the `Description` column.

2. **Unbounded result set**: Even after the scan, all 1000 matching rows are materialized, serialized, and sent over the wire. This holds a DB connection for ~50-100ms, identical to the GetProducts bottleneck.

Combined with GetProducts, these two endpoints account for ~11.2% of traffic but consume disproportionate connection pool time, compounding the exhaustion cascade.

## Proposed Fixes

1. **Add result limiting and pagination**: Add `pageSize` (default 20, max 50) and `pageIndex` parameters. Apply `.Skip().Take()` to cap the result set. This eliminates the unbounded result problem regardless of how many rows match.

2. **Fallback guard**: When `q` is null/empty (lines 114-126), the method returns ALL products — apply the same pagination to this fallback path.

## Expected Impact

- p95 latency: Similar reduction profile to GetProducts — per-request DB time drops from ~100ms to ~10ms. Combined with the GetProducts fix, connection pool utilization should drop below saturation.
- RPS: Contributes to the overall throughput recovery from 7.2 to 50+ RPS.
- The LIKE scan cost remains but is bounded by Take(), keeping connection hold time short.

