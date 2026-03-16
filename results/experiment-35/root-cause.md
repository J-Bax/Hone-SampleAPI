# Root Cause Analysis — Experiment 35

> Generated: 2026-03-16 12:41:42 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 535.373775ms | 7546.103045ms |
| Requests/sec | 1122.1 | 125.5 |
| Error Rate | 0% | 0% |

---
# Set minimum log level to Warning to reduce per-request logging overhead

> **File:** `SampleApi/Program.cs` | **Scope:** narrow

## Evidence

The CPU profiler identified console logging as a measurable overhead source:

> `Microsoft.Extensions.Logging.Console.AnsiParser.Parse` — 0.09% exclusive CPU
> Combined with `Logger.IsEnabled` checks (510 samples), logging adds measurable overhead.

At `Program.cs:4-15`, the application uses default logging configuration with no explicit minimum level:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
// ... no logging configuration ...
```

The default ASP.NET Core logging level is `Information` in Development, which causes EF Core to log every SQL query, Kestrel to log every request, and the DI container to log service resolution. Under 500 concurrent VUs firing 18 requests each, this generates thousands of log entries per second — each requiring string formatting, ANSI color parsing, and synchronous console writes.

The profiler also noted `SemaphoreSlim.Wait` (synchronous, not async) with `SpinWait.SpinOnceCore` (307 samples), which is consistent with the console logger's internal synchronization mechanism blocking threads under high throughput.

## Theory

At Information level, every EF Core query execution logs the SQL text (including parameter values), every HTTP request logs method/path/status, and middleware logs pipeline execution. Under high concurrency, the synchronous console logger becomes a bottleneck: its internal semaphore serializes writes, causing thread pool threads to spin-wait. This manifests as the `SemaphoreSlim.Wait` + `SpinWait.SpinOnceCore` samples in the CPU profile. Raising the minimum level to Warning eliminates ~95% of log entries, removing both the formatting overhead and the synchronization contention.

## Proposed Fixes

1. **Add logging configuration in Program.cs** after `CreateBuilder`: Add `builder.Logging.SetMinimumLevel(LogLevel.Warning);` to suppress Information and Debug log entries. This is a single line addition that affects all logging providers.

## Expected Impact

- p95 latency: ~3-8ms reduction across all requests from eliminated log formatting and reduced thread contention
- Thread pool efficiency: Removing synchronous console logger contention frees threads for request processing
- This affects 100% of traffic but per-request savings are small
- Overall p95 improvement: ~1-1.5%

