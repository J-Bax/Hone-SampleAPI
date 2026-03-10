# Enable DbContext pooling to reduce per-request allocations

> **File:** `SampleApi/Program.cs` | **Scope:** narrow

## Evidence

At `Program.cs:12–13`:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
```

The standard `AddDbContext` registers `AppDbContext` as a **scoped service**, meaning a new instance is created and disposed for every HTTP request. At 1,345 RPS, that's 1,345 DbContext constructions and disposals per second.

The memory-gc report shows:
- Allocation rate: **610 MB/sec** — extremely high for an API workload
- Gen1/Gen0 ratio: **0.88** — mid-life crisis pattern where objects survive Gen0 but die in Gen1
- Gen0 max pause: **46.9ms** — near the p95-impact threshold
- 252 total GC pauses in 120 seconds

The CPU profile shows DI resolution (`ResolveService + Dictionary.FindValue`) at 0.26% exclusive and async state machine overhead at 0.75% — both amplified by per-request DbContext lifecycle.

The `AppDbContext` constructor (`AppDbContext.cs:8`) only accepts `DbContextOptions<AppDbContext>` — the exact signature required for pooling:

```csharp
public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
```

No custom state is stored on the DbContext (only `DbSet` properties at lines 12–17), making it safe for pooling.

## Theory

Each DbContext construction allocates: the context object itself, internal change tracker data structures (StateManager, InternalEntityEntryFactory, etc.), identity resolution maps, and service references. Disposal tears these down. With `AddDbContextPool`, EF Core maintains a pool of pre-initialized DbContext instances — on each request, an existing instance is checked out, its change tracker is reset (cheap), and on request completion it's returned to the pool rather than disposed.

This eliminates the allocation/deallocation cycle for DbContext internals on every request. At 1,345 RPS, even modest per-instance savings (a few KB of allocations) compound into measurable reductions in allocation rate and GC frequency. The mid-life crisis GC pattern suggests many objects are living just long enough to escape Gen0 — DbContext internals that survive through an async request pipeline fit this profile exactly.

## Proposed Fixes

1. **Switch to AddDbContextPool:** Change `AddDbContext` to `AddDbContextPool` at `Program.cs:12`:
   ```csharp
   builder.Services.AddDbContextPool<AppDbContext>(options =>
       options.UseSqlServer(...));
   ```
   The default pool size is 1024, sufficient for the 500-VU load test. No other code changes required — the DbContext API is identical.

## Expected Impact

- Allocation rate: ~3–5% reduction from eliminating per-request DbContext construction overhead
- GC pressure: fewer Gen0 collections, potentially reducing the max Gen0 pause below the 46.9ms threshold
- p95 latency: ~1–3% indirect improvement from reduced GC pause jitter
- RPS: ~2–5% throughput improvement (documented in Microsoft benchmarks for DbContext pooling)
- Zero risk of behavioral change — the EF Core API surface is identical with pooling enabled
