# Root Cause Analysis — Experiment 34

> Generated: 2026-03-16 12:17:08 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 543.75981ms | 7546.103045ms |
| Requests/sec | 1140.3 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add Select projection to paginated product query excluding Description

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:56-60`, the paginated product query loads full `Product` entities including the `Description` text field:

```csharp
Products = await query
    .Skip((CurrentPage - 1) * PageSize)
    .Take(PageSize)
    .AsNoTracking()
    .ToListAsync();
```

With `PageSize = 24` (line 15), this loads up to 24 full Product entities per request. The `Description` field is a nullable string with no length constraint in the model (`Product.cs:10`), potentially containing substantial text per product.

For comparison, the API's `ProductsController.GetProducts()` (lines 25-36) already uses a Select projection that excludes Description — this was added in experiment 27. The Razor Products page was attempted in experiment 30 but had a build failure, so this optimization has never successfully been applied.

The CPU profiler shows `ToListAsync` at 5.5% inclusive CPU with heavy `TryReadColumnInternal` and `UnicodeEncoding.GetChars` beneath it, confirming over-fetching of string columns.

## Theory

The Products page renders a grid of product cards showing Name, Price, and Category — Description is not displayed on the listing page. Loading 24 Description strings per request wastes SQL Server I/O bandwidth, TDS parsing CPU, and .NET heap allocations. Under 500 concurrent VUs, this amplifies into measurable throughput loss. The build failure in experiment 30 likely resulted from a type mismatch in the Select projection; the fix needs to project into `Product` objects (matching the `Products` property type) rather than anonymous types.

## Proposed Fixes

1. **Add Select projection before ToListAsync** (line 56-60): Insert `.Select(p => new Product { Id = p.Id, Name = p.Name, Price = p.Price, Category = p.Category, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt })` between `.AsNoTracking()` and `.ToListAsync()`. This matches the pattern already used in `ProductsController.GetProducts()` and ensures the `List<Product>` property type is preserved.

## Expected Impact

- p95 latency: ~10-15ms reduction per request from avoiding 24 Description column reads
- Allocation reduction: Eliminates 24 string allocations per request for Description
- Overall p95 improvement: ~1.5-2%, as this endpoint accounts for ~5.5% of traffic

