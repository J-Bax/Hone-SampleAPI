# Root Cause Analysis — Experiment 2

> Generated: 2026-03-23 03:06:58 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 27950.345ms | 27950.345ms |
| Requests/sec | 20.1 | 20.1 |
| Error Rate | 100% | 100% |

---
# Unbounded product queries return all 1000 rows per request

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:23-38`, `GetProducts()` loads the entire product catalog without pagination:

```csharp
public async Task<ActionResult<IEnumerable<Product>>> GetProducts()
{
    var products = await _context.Products
        .AsNoTracking()
        .Select(p => new Product { ... })
        .ToListAsync();
    return Ok(products);
}
```

At `ProductsController.cs:93-127`, `SearchProducts` with `q=Product` matches **all 1000 products** because every seeded product is named `"Product XXXX - Category"` (`SeedData.cs:42`). The LIKE pattern `%Product%` triggers a full table scan:

```csharp
.Where(p => EF.Functions.Like(p.Name, $"%{q}%") ||
            EF.Functions.Like(p.Description, $"%{q}%"))
```

The k6 scenario calls both endpoints every iteration:
```javascript
const listRes = http.get(`${BASE_URL}/api/products`);
const searchRes = http.get(`${BASE_URL}/api/products/search?q=Product`);
```

Under 500 VUs, this means ~500 concurrent requests each serializing 1000 Product objects into JSON. Each response is ~200KB+, creating enormous network I/O and serialization CPU load.

## Theory

Returning unbounded result sets has compounding costs under concurrency: (1) SQL Server must scan and return all rows, holding the connection longer; (2) EF Core must materialize 1000 entities per request; (3) the JSON serializer must process 1000 objects; (4) the response payload is ~200KB per request. With 500 VUs calling these endpoints, the server processes ~1 million product entities per second just for these two endpoints. This monopolizes DB connections (worsening pool exhaustion), consumes CPU on serialization, and saturates network bandwidth.

The `LIKE '%Product%'` pattern with a leading wildcard prevents SQL Server from using the index on `Name`, forcing a full clustered index scan on every call.

## Proposed Fixes

1. **Add pagination to `GetProducts`:** Add `[FromQuery] int page = 1, [FromQuery] int pageSize = 20` parameters. Apply `.Skip((page - 1) * pageSize).Take(pageSize)` before `ToListAsync()`. This reduces per-request row count from 1000 to 20 (a 98% reduction).

2. **Limit `SearchProducts` result count:** Add `.Take(50)` to the search query at line 110 to cap results. Optionally switch from `LIKE '%q%'` to `LIKE 'q%'` (prefix match) at lines 99-100 to enable index usage when the search term appears at the start of the name.

## Expected Impact

- p95 latency for these endpoints: ~500ms reduction per request (smaller payloads, faster queries)
- DB connection hold time reduced ~50x per request (20 rows vs 1000)
- Network bandwidth reduced by ~98% for these endpoints
- Overall p95 improvement: ~5% (after connection pool fix brings p95 to ~4000ms)

