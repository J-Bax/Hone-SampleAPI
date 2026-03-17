# Root Cause Analysis — Experiment 43

> Generated: 2026-03-16 23:23:25 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 497.12074ms | 7546.103045ms |
| Requests/sec | 1231 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add Select projection to GetProduct single-entity endpoint

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:46-48`, the single-product endpoint loads the full entity without projection:

```csharp
var product = await _context.Products
    .AsNoTracking()
    .FirstOrDefaultAsync(p => p.Id == id);
```

This fetches ALL columns including `Description` (potentially up to `nvarchar(max)` — no `HasMaxLength` constraint is configured in `AppDbContext.cs:23-30` for Description). Every other product read path in the codebase already uses a Select projection that excludes Description:

- `ProductsController.cs:27-35` (GetProducts) — excludes Description ✓
- `ProductsController.cs:65-73` (GetProductsByCategory) — excludes Description ✓
- `ProductsController.cs:101-109` (SearchProducts) — excludes Description ✓
- `Pages/Index.cshtml.cs:34` (Home page) — excludes Description ✓
- `Pages/Products/Index.cshtml.cs:60` (Products page) — excludes Description ✓

The CPU profiler shows ~21% of CPU in SQL data reading (TryReadColumnInternal, TryReadSqlStringValue) and ~7.2% in Unicode encoding (GetCharCount, GetChars) — both directly proportional to the volume of string data fetched. The k6 scenario calls `GET /api/products/{randomId}` once per VU iteration (~5.56% of traffic).

## Theory

The Description field is a potentially large text column read from SQL, decoded from Unicode bytes, allocated as a .NET string, tracked by the materializer's type-casting pipeline, and then serialized to JSON — only for the client to receive data it likely doesn't need for a product detail API call (the Razor detail page already uses its own optimized query). Each step in this chain (SQL read → Unicode decode → string alloc → JSON serialize) appears in the profiler's top CPU consumers. Eliminating this column from the query removes work from all four hot paths simultaneously.

## Proposed Fixes

1. **Add Select projection matching existing pattern:** At `ProductsController.cs:46-48`, replace the bare `FirstOrDefaultAsync` with a projection:
   ```csharp
   var product = await _context.Products
       .AsNoTracking()
       .Where(p => p.Id == id)
       .Select(p => new Product
       {
           Id = p.Id, Name = p.Name, Price = p.Price,
           Category = p.Category, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt
       })
       .FirstOrDefaultAsync();
   ```
   This matches the exact projection pattern used by all other product endpoints.

## Expected Impact

- **p95 latency:** Estimated 3–8ms reduction for this endpoint (eliminating large string read + allocation + serialization)
- **RPS:** Marginal improvement from reduced per-request CPU and memory allocation
- **Overall p95 improvement:** ~0.5–1% — single-entity endpoint at 5.56% of traffic, but Description can be a large string

