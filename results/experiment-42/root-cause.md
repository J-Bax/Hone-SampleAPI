# Root Cause Analysis — Experiment 42

> Generated: 2026-03-16 22:58:44 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 497.12074ms | 7546.103045ms |
| Requests/sec | 1231 | 125.5 |
| Error Rate | 0% | 0% |

---
# Configure JSON to skip null property serialization

> **File:** `SampleApi/Program.cs` | **Scope:** narrow

## Evidence

The CPU profiler identifies ~10.7% of attributable application CPU in JSON serialization methods, with **WriteNullSection accounting for 481 samples** — a significant fraction devoted purely to writing `"property": null` entries.

At `Program.cs:10`, controllers are registered with default JSON options:

```csharp
builder.Services.AddControllers();
```

The default `System.Text.Json` configuration serializes all properties, including nulls. Multiple model types have frequently-null properties:

- `Product.cs:10` — `public string? Description { get; set; }` (excluded by Select projections in list endpoints, but the projected `new Product { ... }` leaves Description = null)
- `Product.cs:14` — `public DateTime? UpdatedAt { get; set; }` (null for products never updated)
- `Review.cs:12` — `public string? Comment { get; set; }` (excluded by projections but still present on entity)

Every product list response (GetProducts returns ~1000 products, SearchProducts returns ~1000 matching "Product") serializes `"description": null` and `"updatedAt": null` for each item — 2000+ null property writes per single list response.

## Theory

When Select projections create `new Product { Id = ..., Name = ..., Price = ... }` without setting Description or UpdatedAt, those properties default to null. System.Text.Json still writes them as `"description": null, "updatedAt": null` in the output. At ~1000 products per list/search response across ~11% of total traffic (two list-style product endpoints), this generates substantial serialization work: buffer allocation for the null tokens, property name encoding, and output buffer management — all for data the client doesn't need.

The profiler's WriteNullSection (481 samples), WritePropertyNameSection, and WriteStringMinimized methods collectively show the serializer spending significant cycles on these no-value fields. Additionally, anonymous type responses (cart, order details) may contain null-valued properties that add further overhead.

## Proposed Fixes

1. **Configure DefaultIgnoreCondition.WhenWritingNull globally:** At `Program.cs:10`, change `AddControllers()` to `AddControllers(options => options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)`. This eliminates null property serialization across ALL API responses without changing any endpoint logic.

## Expected Impact

- **p95 latency:** Estimated 5–12ms reduction across all endpoints, with larger savings on list endpoints serializing hundreds of objects
- **RPS:** ~2–3% improvement from reduced CPU time in serialization pipeline
- **Overall p95 improvement:** ~1.5–2.5% — modest per-request savings but compounded across 100% of traffic

