# Root Cause Analysis — Experiment 18

> Generated: 2026-03-10 06:21:30 | Classification: narrow — Response compression middleware can be added by calling builder.Services.AddResponseCompression() and app.UseResponseCompression() in Program.cs, modifying only this single file without adding NuGet packages, changing endpoints, or altering the database schema.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 403.18154ms | 888.549155000001ms |
| Requests/sec | 1365.2 | 683.2 |
| Error Rate | 0% | 0% |

---
# Enable response compression middleware for large JSON payloads

> **File:** `SampleApi/Program.cs` | **Scope:** narrow

## Evidence

At `Program.cs:15-29`, the middleware pipeline has no response compression configured:

```csharp
var app = builder.Build();
// ... no AddResponseCompression / UseResponseCompression
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();
```

`GET /api/products` and `GET /api/products/search?q=Product` each return ~1,000 Product JSON objects per response — approximately **200KB+ of uncompressed JSON** per response. The CPU profiler shows JSON serialization at **~2.3% exclusive CPU** (`StringConverter.Write` at 0.88%, `ObjectDefaultConverter.OnTryWrite` at 0.27%, plus encoding/escaping overhead).

With 500 VUs at peak load, Kestrel is managing ~100MB of in-flight uncompressed response data across concurrent requests. Current CPU utilization is only **18.74%**, leaving significant headroom.

## Theory

Without compression, Kestrel allocates and writes full-size response buffers (~200KB each) through its I/O pipeline. The `System.IO.Pipelines` infrastructure must manage, flush, and reclaim these large buffers for every response. Gzip compression typically achieves **80–90% reduction** on repetitive JSON (the product JSON has highly repetitive field names and structure), reducing each response to ~20–30KB. This cuts buffer allocation pressure, reduces Kestrel's `PipeWriter` flush frequency, and decreases TCP write system calls. k6 natively sends `Accept-Encoding: gzip` and handles decompression transparently.

## Proposed Fixes

1. **Add response compression middleware:** In `Program.cs`, add `builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; })` after the existing service registrations, and add `app.UseResponseCompression()` before `app.UseStaticFiles()` (line 24). This enables gzip compression for all JSON API responses above the default minimum size threshold.

## Expected Impact

- p95 latency: **3–6% reduction** (~379–391ms). Smaller response buffers reduce per-request memory allocation and Kestrel I/O overhead.
- RPS: **3–5% increase** (~1,400–1,430 RPS). Reduced I/O work per response frees CPU for additional request processing.
- Allocation rate: moderate reduction from smaller response body buffers. The compression CPU cost (~2–3% overhead) is offset by the 18.74% current utilization headroom.

