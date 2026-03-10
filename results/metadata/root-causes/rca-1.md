# In-memory caching for product catalog read endpoints

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** architecture

## Evidence

At `ProductsController.cs:25`, the `GetProducts` endpoint loads the entire product catalog on every request:

```csharp
var products = await _context.Products.AsNoTracking().ToListAsync();
```

At `ProductsController.cs:70-76`, the `SearchProducts` endpoint with `q=Product` (the k6 test's search term) matches all ~1000 products because every seeded product name starts with "Product":

```csharp
var filtered = await _context.Products.AsNoTracking()
    .Where(p => p.Name.Contains(q) || (p.Description != null && p.Description.Contains(q)))
    .ToListAsync();
```

The CPU profiler shows `ToListAsync` at **28.5% inclusive** and `MoveNextAsync` at **24% inclusive** — DB materialization dominates CPU time. The memory profiler reports **627 MB/sec** allocation rate with a peak heap of **1,271 MB**.

The k6 scenario calls both endpoints once per iteration (lines 38 and 52 of `baseline.js`), so each VU iteration materializes ~2,000 Product entities from the database. The product catalog is read-only during the load test (no k6 writes to products).

## Theory

With ~105 k6 iterations/sec at peak load, the server executes ~210 full-table queries/sec against the 1,000-row Products table. Each query materializes all 1,000 entities including the `Description` column (`nvarchar(max)`), triggering the TDS character-by-character parsing hotspot (`TryReadChar` at 1.71% exclusive). The materialized entities are then JSON-serialized (~2.3% exclusive CPU) and immediately discarded, only to be re-queried milliseconds later by the next request. This is pure waste — the same immutable data is fetched, materialized, serialized, and GC'd hundreds of times per second.

## Proposed Fixes

1. **IMemoryCache with short TTL:** Add `builder.Services.AddMemoryCache()` in `Program.cs`. Inject `IMemoryCache` into `ProductsController`. Cache the full product list with a 5–10 second TTL. Serve `GetProducts` from cache. For `SearchProducts` and `GetProductsByCategory`, either filter the cached list in-memory or cache per-query-key results separately. Apply at lines 25, 55–57, and 70–76.

## Expected Impact

- p95 latency: **15–25% reduction** (~300–340ms). The 28.5% ToListAsync CPU cost drops proportionally to cache hit rate (near 100% with short TTL under sustained load).
- RPS: **20–30% increase** (~1,650–1,750 RPS). Freed CPU cycles allow Kestrel to serve more concurrent requests.
- Allocation rate: significant reduction since cached responses avoid entity materialization and change tracker overhead per request.
