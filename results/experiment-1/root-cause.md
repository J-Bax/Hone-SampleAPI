# Root Cause Analysis — Experiment 1

> Generated: 2026-03-23 03:04:54 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 27950.345ms | 27950.345ms |
| Requests/sec | 20.1 | 20.1 |
| Error Rate | 100% | 100% |

---
# Connection pool exhaustion causes 100% error rate under load

> **File:** `Program.cs` | **Scope:** narrow

## Evidence

At `Program.cs:15-16`, the DbContext is registered without pooling:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
```

The connection string in `appsettings.json:3` specifies no `Max Pool Size`:

```json
"DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=HoneSampleDb;Trusted_Connection=True;MultipleActiveResultSets=true"
```

The default SQL Server connection pool size is **100 connections**. The k6 scenario ramps to **500 concurrent VUs**, each executing 18 sequential HTTP requests. At peak load, ~500 concurrent requests compete for 100 pooled connections. The remaining 400 requests queue up and eventually timeout (default 15s), producing `SqlException: Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.` This cascades into 100% HTTP 500 errors.

Additionally, `AddDbContext` allocates a fresh `DbContext` per request. Under 500 VUs, this means ~500 simultaneous DbContext instances with full change-tracking overhead, each competing for scarce pooled connections.

## Theory

SQL Server's ADO.NET connection pool defaults to `Max Pool Size=100`. When concurrent demand exceeds pool capacity, new requests block waiting for a connection to be returned. After the pool wait timeout expires (~15s by default), a `SqlException` is thrown, which ASP.NET surfaces as HTTP 500. Under 500 VUs with zero think-time, demand permanently exceeds capacity, causing every request to eventually timeout. This explains both the 100% error rate and the ~28s p95 latency (requests wait ~15s for pool timeout + processing overhead).

Using `AddDbContext` instead of `AddDbContextPool` also means each request instantiates a new `DbContext`, adding GC pressure and preventing EF Core from reusing internal data structures. Under extreme concurrency, this amplifies memory allocation rates and increases Gen0/Gen1 GC pauses.

No transient fault retry policy is configured, so pool-exhaustion exceptions immediately fail the request rather than retrying after a brief backoff.

## Proposed Fixes

1. **Switch to `AddDbContextPool` with increased pool size:** Replace `AddDbContext` with `AddDbContextPool<AppDbContext>` at `Program.cs:15`. This reuses DbContext instances from a pool, reducing allocation overhead and improving connection reuse. Set the pool size to 1024 (the EF Core default is 1024, which accommodates 500 VUs comfortably).

2. **Increase `Max Pool Size` in the connection string and add retry logic:** In `appsettings.json:3`, add `Max Pool Size=512;Connection Timeout=30` to the connection string. In the `UseSqlServer` options, enable `options.EnableRetryOnTransientFailures()` to handle transient pool-exhaustion errors gracefully.

## Expected Impact

- Error rate: Expected to drop from 100% to <5% (threshold target)
- p95 latency: Expected to drop from ~28000ms to ~3000-5000ms (connections no longer timeout)
- RPS: Expected to increase from 20 to 100+ as requests complete successfully
- This single fix addresses the root cause of the catastrophic failure mode. All other optimizations are secondary until connection pool exhaustion is resolved.

