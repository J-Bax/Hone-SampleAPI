# Root Cause Analysis — Experiment 10

> Generated: 2026-03-15 08:17:35 | Classification: narrow — All fixes—adding .AsNoTracking() to read-only queries and replacing client-side table scans (lines 34, 41, 65) with server-side .Where() filters—are contained within this single PageModel file with no dependency, schema, or API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 531.09704ms | 1596.242785ms |
| Requests/sec | 1195.2 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# Change tracking overhead on bulk queries and CartItems table scan in POST handler

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Detail.cshtml.cs:34`, OnGetAsync loads the entire Reviews table **with change tracking** enabled:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
```

At `Detail.cshtml.cs:41`, it loads the entire Products table **with change tracking**:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

These queries materialize ~2000 reviews + 1000 products = **3000 tracked entities per OnGetAsync call**. Since OnGetAsync is called twice per k6 iteration — once directly for GET, and again from OnPostAsync at line 88 (`return await OnGetAsync(productId)`) — each iteration materializes ~6000 change-tracked entities.

At `Detail.cshtml.cs:65-67`, OnPostAsync loads the **entire CartItems table** to find a single matching item:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var existing = allCartItems.FirstOrDefault(c =>
    c.SessionId == sessionId && c.ProductId == productId);
```

The CPU profile confirms heavy **SortedDictionary/SortedSet enumeration (~6255 samples)**, which is EF Core's internal change tracker data structure. The memory profile shows an **846 MB/sec allocation rate** with **2.8GB peak heap** and a severely inverted GC generation distribution (Gen2: 46 > Gen1: 34 > Gen0: 7), indicating massive short-lived allocations promoting rapidly through generations. Max GC pause is **210ms**, directly inflating the 531ms p95.

> **NOTE:** Experiment 5 attempted to fix "Full table scans of Reviews and Products" in this file and regressed. This optimization is **different** — it does NOT restructure the queries or change filtering logic. It only: (1) adds `AsNoTracking()` to eliminate change tracking overhead on read-only bulk queries in OnGetAsync, and (2) replaces the CartItems full table scan in OnPostAsync with a server-side filtered query.

## Theory

EF Core's change tracker creates a snapshot of every tracked entity for dirty detection. For each of the ~3000 entities loaded per OnGetAsync call, the tracker allocates:
- An `EntityEntry` state object (~200 bytes)
- A snapshot copy of all property values (~200–400 bytes)
- `SortedDictionary`/`SortedSet` entries for identity map lookups (~100 bytes)

Total overhead: ~500 bytes/entity × 3000 entities × 2 calls/iteration ≈ **3MB of avoidable allocation per iteration**. Under 500 VUs with continuous iterations, this generates hundreds of MB/sec of garbage that immediately becomes eligible for collection.

This drives the inverted GC pattern: change tracking snapshots survive Gen0/Gen1 (because the tracked collection stays alive for the request duration), promote to Gen2, then become garbage after the response completes. The result is frequent Gen2 collections (one every ~2.6s) with 210ms max pauses that directly inflate p95 latency.

The CartItems table scan in OnPostAsync compounds this by loading all cart items (growing table under load) with change tracking when only one row is needed.

## Proposed Fixes

1. **Add `AsNoTracking()` to OnGetAsync bulk queries:** At lines 34 and 41, chain `.AsNoTracking()` before `.ToListAsync()`. These collections are read-only (used for page rendering) and do not need change tracking. This eliminates ~3MB of per-request allocation overhead without changing query structure.

2. **Replace CartItems full table scan with server-side filter:** At lines 65–67, replace `await _context.CartItems.ToListAsync()` + in-memory `FirstOrDefault` with `await _context.CartItems.FirstOrDefaultAsync(c => c.SessionId == sessionId && c.ProductId == productId)`. Keep tracking enabled since the result may be modified at line 69 (`existing.Quantity += quantity`).

## Expected Impact

- **p95 latency:** Estimated ~25–35ms reduction. The primary mechanism is reduced GC pause frequency and duration from eliminating ~300–600 MB/sec of change tracking allocations. Max GC pause should drop from 210ms to ~140–160ms, and the 5.8% GC pause ratio should decrease to ~3.5–4%.
- **RPS:** Should increase ~3–5% from freed CPU cycles (6255 SortedSet samples + materialization overhead).
- **Error rate:** May decrease if some of the 11.11% errors are caused by GC-induced request timeouts.
- The p95 benefit extends **beyond** the Detail page because reduced GC pauses benefit all concurrent requests.

