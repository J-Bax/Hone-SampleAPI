# Root Cause Analysis — Experiment 24

> Generated: 2026-03-16 00:08:46 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 546.113655ms | 7546.103045ms |
| Requests/sec | 1100.1 | 125.5 |
| Error Rate | 0% | 0% |

---
# Replace AddDbContext with AddDbContextPool to reduce allocation pressure

> **File:** `SampleApi/Program.cs` | **Scope:** narrow

## Evidence

At `Program.cs:12`, the service registration uses `AddDbContext`:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
```

This creates and disposes a new `AppDbContext` instance for every HTTP request. At 1,100 RPS that is 1,100 DbContext allocations and disposals per second.

The CPU profile shows 861 samples in `DependencyInjection.ResolveService` plus 705 in `ServiceCacheKey` dictionary lookups — direct evidence of per-request DI resolution overhead.

The memory-GC report confirms an extreme allocation rate of **433 MB/sec** (51 GB total over 118 s) with a Gen0/Gen1 near-parity "mid-life crisis" pattern (106 Gen0 vs 92 Gen1 collections). Max Gen1 GC pause is **113.6 ms**, which alone consumes ~20% of the 546 ms p95 budget.

## Theory

`DbContext` is a heavyweight object: it allocates internal dictionaries, change trackers, identity maps, and compiled query caches on each construction. Under high concurrency these short-lived objects survive Gen0 (because the async pipeline keeps them alive through `await` boundaries) and get promoted to Gen1, triggering the observed mid-life crisis GC pattern.

`AddDbContextPool` maintains a pool of pre-allocated `AppDbContext` instances, resetting their state via `DbContext.ResetState()` instead of constructing/disposing new objects. This eliminates:
- Per-request allocation of DbContext internals
- Per-request DI container resolution overhead
- Downstream Gen0→Gen1 promotion pressure

## Proposed Fixes

1. **Use AddDbContextPool:** At `Program.cs:12`, replace `AddDbContext<AppDbContext>` with `AddDbContextPool<AppDbContext>`. Optionally set pool size (default 1024 is fine for this workload). The rest of the code is fully compatible — pooled contexts behave identically to non-pooled contexts for consumers.

## Expected Impact

- Reduced allocation rate by an estimated 5-15%, lowering Gen0/Gen1 collection frequency
- Reduced max GC pause severity (currently 113.6 ms), directly improving p95 tail latency
- Reduced DI resolution CPU overhead (~1,500 samples)
- Estimated p95 improvement: ~2% (10-15 ms off 546 ms) from combined allocation + GC + DI savings

