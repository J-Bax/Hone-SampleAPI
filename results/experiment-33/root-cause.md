# Root Cause Analysis — Experiment 33

> Generated: 2026-03-16 11:52:28 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 537.728965ms | 7546.103045ms |
| Requests/sec | 1124.5 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add Select projections to Reviews and RelatedProducts queries on Detail page

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:34-38`, the Reviews query loads full entities including the `Comment` field (up to 2000 chars per review):

```csharp
Reviews = await _context.Reviews
    .AsNoTracking()
    .Where(r => r.ProductId == id)
    .OrderByDescending(r => r.CreatedAt)
    .ToListAsync();
```

At `Pages/Products/Detail.cshtml.cs:42-46`, the RelatedProducts query loads full Product entities including `Description`:

```csharp
RelatedProducts = await _context.Products
    .AsNoTracking()
    .Where(p => p.Category == Product.Category && p.Id != id)
    .Take(4)
    .ToListAsync();
```

The CPU profiler confirms SQL data reading dominates: `TryReadColumnInternal` at 0.64% exclusive CPU with `UnicodeEncoding.GetChars` at 2,546 samples, indicating wide string columns being read unnecessarily. The memory profiler reports 281 MB/sec allocation throughput — materializing large text fields into strings contributes to this churn.

The page only needs `Id`, `ProductId`, `CustomerName`, `Rating`, and `CreatedAt` from Reviews (Comment is not rendered in the review summary list). For RelatedProducts, only `Id`, `Name`, `Price`, and `Category` are needed for the card display.

## Theory

Both queries fetch every column from their respective tables via implicit `SELECT *`. The `Comment` field (VARCHAR(2000)) and `Description` field are large text columns that require significant I/O to read from SQL Server, TDS wire bytes to parse, and managed heap allocations to materialize as .NET strings — all of which are immediately discarded after the response is rendered. Under high concurrency (500 VUs), this multiplies into substantial wasted CPU time in TDS parsing and unnecessary GC pressure from short-lived string allocations. The `OnGetAsync` method is called by both GET and POST handlers (POST calls `OnGetAsync` at line 87), doubling the impact.

## Proposed Fixes

1. **Add Select projection to Reviews query** (line 34-38): Add `.Select(r => new Review { Id = r.Id, ProductId = r.ProductId, CustomerName = r.CustomerName, Rating = r.Rating, CreatedAt = r.CreatedAt })` before `.ToListAsync()` to exclude `Comment`.

2. **Add Select projection to RelatedProducts query** (line 42-46): Add `.Select(p => new Product { Id = p.Id, Name = p.Name, Price = p.Price, Category = p.Category })` before `.ToListAsync()` to exclude `Description`, `CreatedAt`, `UpdatedAt`.

## Expected Impact

- p95 latency: ~15-25ms reduction per affected request from reduced SQL I/O, TDS parsing, and string allocations
- RPS: Minor improvement from reduced per-request CPU time
- GC pressure: Reduced string allocations (Comment can be 2KB × N reviews per request)
- Overall p95 improvement: ~3-5%, as this endpoint accounts for ~11% of total traffic (GET + POST both execute OnGetAsync)

