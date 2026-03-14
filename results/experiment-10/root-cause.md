# Root Cause Analysis — Experiment 10

> Generated: 2026-03-14 16:26:59 | Classification: narrow — DbContext pooling is enabled by changing line 12-13 from `AddDbContext` to `AddPooledDbContext`, which is a pure configuration change contained to Program.cs with no impact on APIs, dependencies, database schema, or tests.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 496.6681ms | 2054.749925ms |
| Requests/sec | 1295.4 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Enable DbContext pooling to reduce per-request allocation and GC pressure

> **File:** `SampleApi/Program.cs` | **Scope:** narrow

## Evidence

At `Program.cs:12-13`, the DbContext is registered without pooling:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
```

This creates a **new DbContext instance per request** via DI, including its change tracker, state manager, and internal data structures. At ~1295 req/sec, that is 1295 DbContext allocations and disposals per second.

The CPU profiler confirms DI overhead: **ResolveService at 0.11% (825 samples)**. More significantly, change tracker data structures dominate:
- **StateManager.StartTrackingFromQuery**: 0.75% inclusive (794 samples)
- **SortedDictionary enumeration cluster**: 1.04% exclusive (~7,950 samples)
- **NavigationFixer.InitialFixup**: 556 samples
- **EntityReferenceMap.Update**: 566 samples

The memory profiler shows **779 MB/sec allocation rate** with **92% Gen0→Gen1 promotion rate** — meaning most allocated objects survive just past the Gen0 threshold. This is the classic pattern of per-request objects (like DbContext) that live across async continuations. The **max GC pause of 516.6ms** exceeds the p95 latency target and likely causes tail-latency spikes.

## Theory

`AddDbContext<T>` registers the DbContext as a **transient/scoped** service — a new instance is constructed for every HTTP request. Each construction allocates the DbContext itself, its ChangeTracker, StateManager, EntityReferenceMap (backed by SortedDictionary), and other internal state. At 1295 req/sec, this generates substantial ephemeral allocation pressure.

These objects live for the duration of the request (spanning multiple `await` points), causing them to survive Gen0 collection and promote into Gen1 — exactly matching the observed 92% promotion rate. The high promotion rate forces frequent Gen1 collections (121 vs 131 Gen0) and occasional expensive compacting collections with up to 516ms pauses.

`AddDbContextPool<T>()` maintains a pool of pre-allocated DbContext instances. When a request needs a DbContext, it retrieves one from the pool (near-zero allocation); when the request ends, the context is reset and returned. This eliminates the per-request allocation/disposal cycle.

## Proposed Fixes

1. **Replace `AddDbContext` with `AddDbContextPool`** at `Program.cs:12`:
   Change `builder.Services.AddDbContext<AppDbContext>(options => ...)` to `builder.Services.AddDbContextPool<AppDbContext>(options => ...)`. The default pool size (1024) is sufficient for 500 VUs. No other code changes are needed — `AppDbContext` has a simple constructor (`AppDbContext.cs:8`) that accepts `DbContextOptions<AppDbContext>`, which is compatible with pooling.

## Expected Impact

- p95 latency: estimated 2-4% improvement from reduced per-request overhead
- Allocation rate should decrease measurably (fewer DbContext + internal structure allocations per second)
- Gen0→Gen1 promotion rate should decrease, reducing Gen1 collection frequency
- Max GC pause should decrease (less heap pressure → less aggressive compaction)
- The GC-related tail latency spikes that blow out p95 should become less frequent

