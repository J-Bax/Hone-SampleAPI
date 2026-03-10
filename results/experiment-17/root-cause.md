# Root Cause Analysis — Experiment 17

> Generated: 2026-03-10 05:58:44 | Classification: narrow — Single-file query optimization changing GetProduct's FindAsync to AsNoTracking; implements internal DbContext behavior without altering API contract, dependencies, migrations, or test requirements.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 403.18154ms | 888.549155000001ms |
| Requests/sec | 1365.2 | 683.2 |
| Error Rate | 0% | 0% |

---
# Convert GetProduct from tracked FindAsync to AsNoTracking query

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:35`, the `GetProduct` endpoint uses tracked `FindAsync`:

```csharp
var product = await _context.Products.FindAsync(id);
```

The k6 scenario calls `GET /api/products/{id}` once per iteration (line 47 of `baseline.js`), totaling ~1,365 calls/sec. Each call creates a change-tracking snapshot of the returned Product entity.

The memory profiler reports a **Gen1:Gen0 ratio of 0.90** — nearly every Gen0 collection promotes objects to Gen1, indicating mid-lived allocations (like EF tracking snapshots) that survive the Gen0 threshold but die shortly after. Total allocation rate is **627 MB/sec** with **262 GC collections** in 120s.

## Theory

`FindAsync` first checks the DbContext change tracker (wasted work on a fresh per-request scope), then queries the database and creates an internal snapshot of the entity's original property values for dirty-detection on `SaveChanges`. Since `GetProduct` is a read-only endpoint that never calls `SaveChanges`, this snapshot allocation is pure overhead. At 1,365 RPS, these snapshots contribute to the abnormal Gen1 promotion rate — they survive Gen0 (allocated mid-request) but are collected in Gen1 (discarded after response serialization), exactly matching the 0.90 Gen1:Gen0 ratio signature.

## Proposed Fixes

1. **Replace FindAsync with AsNoTracking query:** At line 35, change to `await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id)`. This eliminates both the change tracker lookup and the snapshot allocation. The query still hits the primary key index and returns the same result.

## Expected Impact

- p95 latency: **2–4% reduction** (~387–395ms). Fewer Gen1 collections mean fewer GC pauses contributing to tail latency.
- Allocation rate: **3–5% reduction**. Each avoided tracking snapshot saves ~500 bytes of snapshot data plus the DetectChanges bookkeeping objects.
- Gen1:Gen0 ratio: should improve toward 0.70–0.80 as fewer mid-lived objects are promoted.

