# Root Cause Analysis — Experiment 8

> Generated: 2026-03-14 15:14:43 | Classification: narrow — Adding AsNoTracking() to read-only query endpoints (GetProducts, GetProduct, GetProductsByCategory, SearchProducts) modifies only method bodies within a single controller file, requires no package changes, no database migrations, and does not alter API contracts or responses.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 482.42257ms | 2054.749925ms |
| Requests/sec | 1321.2 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Add AsNoTracking to all read-only query endpoints

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

All four read endpoints in `ProductsController.cs` materialize entities with full EF Core change tracking enabled:

At line 25 (`GetProducts`):
```csharp
var products = await _context.Products.ToListAsync();
```

At lines 75-78 (`SearchProducts`):
```csharp
var results = await _context.Products
    .Where(p => p.Name.ToLower().Contains(lowerQ) ||
                (p.Description != null && p.Description.ToLower().Contains(lowerQ)))
    .ToListAsync();
```

At lines 55-57 (`GetProductsByCategory`):
```csharp
var filtered = await _context.Products
    .Where(p => p.Category.ToLower() == categoryName.ToLower())
    .ToListAsync();
```

At line 35 (`GetProduct`):
```csharp
var product = await _context.Products.FindAsync(id);
```

The database seeds 1,000 products (`SeedData.cs:37`: `for (int i = 1; i <= 1000; i++)`). `GetProducts()` and `SearchProducts("Product")` (which matches all product names like "Product 0001 - Electronics") each materialize all 1,000 products with full change tracking on every call.

The CPU profiler confirms this is costly: `InternalEntityEntry..ctor` shows 272 exclusive samples from change-tracker construction, and `CastHelpers` aggregate shows ~6,625 samples from EF Core materialization type-checking. The GC analysis reports a catastrophic 664 MB/sec allocation rate averaging 515KB per request, with a 16.2% GC pause ratio and a max pause of 1,931ms.

For each tracked entity, EF Core allocates an `InternalEntityEntry` (~250 bytes) storing original-value snapshots plus identity-map bookkeeping (~50 bytes). For 1,000 products: ~300KB of pure tracking overhead per call, on top of the entity data itself.

## Theory

These four endpoints are read-only — they never call `SaveChangesAsync()`. Yet EF Core creates full change-tracking state for every materialized entity, including original-value snapshot copies and identity-map entries. This is entirely wasted work.

`GetProducts()` and `SearchProducts("Product")` are called once each per VU iteration (~73 calls/sec each at peak), materializing ~1,000 tracked entities per call. That's ~300KB × 2 × 73 = ~43.8 MB/sec of unnecessary allocation solely from tracking overhead. This directly fuels the 664 MB/sec allocation rate that produces the 16.2% GC pause ratio (threshold: 5%) and the catastrophic 1.9-second max GC pause.

The GC analysis confirms: "92% of Gen0 collections escalate to Gen1, meaning objects survive Gen0 at an alarming rate, likely due to mid-request allocations that outlive the ephemeral segment budget." The `InternalEntityEntry` objects, with their many reference fields, create complex GC object graphs that extend tracing time during collection.

## Proposed Fixes

1. **Add `.AsNoTracking()` to all read query chains:** Insert `.AsNoTracking()` before `.ToListAsync()` on lines 25, 57, 71, and 78. For `FindAsync` on line 35, replace with `_context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id)` since `FindAsync` always tracks.

2. **Also add `.AsNoTracking()` to the category existence check** on lines 49-50 (`_context.Categories.AnyAsync(...)`) — `AnyAsync` doesn't materialize entities so this is minor, but the `GetProductsByCategory` product query on line 55 is the key target.

## Expected Impact

- **p95 latency:** ~4% reduction (482ms → ~463ms) from reduced GC pressure across all endpoints
- **RPS:** ~3-4% improvement from freed CPU cycles (less InternalEntityEntry construction, fewer GC pauses)
- **Allocation rate:** ~44 MB/sec reduction (~6.6% of 664 MB/sec), directly reducing GC pause frequency and duration
- **Error rate:** Unlikely to change (11.11% appears structural, not performance-related)

