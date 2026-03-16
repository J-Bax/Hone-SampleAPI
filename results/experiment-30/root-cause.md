# Root Cause Analysis — Experiment 30

> Generated: 2026-03-16 10:50:16 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 534.5337ms | 7546.103045ms |
| Requests/sec | 1101.5 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add Select projection to paginated product query excluding Description

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:56-60`, the paginated product query loads full `Product` entities including the `Description` field:

```csharp
Products = await query
    .Skip((CurrentPage - 1) * PageSize)
    .Take(PageSize)
    .AsNoTracking()
    .ToListAsync();
```

The `Description` field (up to ~100 chars per product, defined at `Models/Product.cs:10`) is fetched for all 24 products per page but is not needed for a product browsing/listing view. By contrast, the API `ProductsController` already applies a Select projection excluding Description (see `Controllers/ProductsController.cs:27-35`), added in experiment 27.

The CPU profile confirms SQL data reading (3.6% exclusive), Unicode string decoding (1.5%), and JSON serialization (1.9%) all scale linearly with column count and data volume. The memory profile shows 295 MB/sec allocation rate driven by EF materialization.

## Theory

Every request to `GET /Products` materializes 24 full `Product` entities through EF Core, including the `Description` string column. This forces SQL Server to read, transmit, and decode the Description column for every row; EF Core to allocate and populate 24 string objects for Description; and Razor's ViewBuffer to serialize them if rendered. With PageSize=24 and ~100-byte Descriptions, that's ~2.4KB of unnecessary data per request flowing through the SQL reader → EF materializer → Razor rendering pipeline. Under 500 concurrent VUs, this amplifies SQL TDS parsing, Unicode decoding, and GC allocation pressure.

## Proposed Fixes

1. **Add Select projection excluding Description:** At `Pages/Products/Index.cshtml.cs:56-60`, add a `.Select()` before `.ToListAsync()` that projects into a `Product` with all fields except `Description` — matching the pattern already used in `ProductsController.GetProducts()` at lines 27-35. This eliminates the Description column from the SQL query entirely.

## Expected Impact

- p95 latency: ~3-8ms reduction on Products page requests (less SQL data reading, EF materialization, and serialization)
- RPS: marginal improvement from reduced CPU work per request
- The Products page is ~5.6% of total k6 traffic (1 of 18 requests per iteration). With ~5.5ms average latency savings, overall p95 improvement is approximately 0.06%.

