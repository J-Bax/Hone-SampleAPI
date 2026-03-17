# Add Select projection to GetReviewsByProduct excluding Comment

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `ReviewsController.cs:49-51`, `GetReviewsByProduct` returns full `Review` entities:

```csharp
var filtered = await _context.Reviews.AsNoTracking()
    .Where(r => r.ProductId == productId)
    .ToListAsync();
```

The `Review` model (`Models/Review.cs:12`) includes `Comment` — an NVARCHAR(2000) field (`Data/AppDbContext.cs:43`).

Seed data (`SeedData.cs:96-97`) generates comments of ~80-120 characters per review:
```csharp
Comment = $"This is review #{reviews.Count + 1} for product {productId}. " +
          $"Rating: {random.Next(1, 6)}/5 stars. Great product overall!",
```

Each product has 1-7 reviews. The k6 scenario calls `GET /api/reviews/by-product/{productId}` every iteration with `seededId(500, 2)`, so this is ~5.6% of total traffic.

The CPU profile specifically identifies this pattern: `SqlDataReader.TryReadColumnInternal` (1.03% inclusive) and `UnicodeEncoding.GetCharCount/GetChars` (0.37%) are driven by reading NVARCHAR columns from the TDS stream. `WillHaveEnoughData` (0.12%) "suggests many small column reads per row — use explicit column projection."

## Theory

Without a `Select` projection, EF generates `SELECT [r].[Id], [r].[Comment], [r].[CreatedAt], [r].[CustomerName], [r].[ProductId], [r].[Rating] FROM Reviews WHERE ProductId = @p`. The `Comment` NVARCHAR(2000) column is read and decoded for every review row even though the k6 scenario only checks HTTP status (never inspects comment content). This wastes:

- SQL Server I/O reading the column from pages
- TDS wire bytes encoding/decoding Unicode strings
- .NET heap allocations for the string objects (contributing to the 293 MB/sec allocation rate)
- JSON serialization time writing comment strings to the response body

## Proposed Fixes

1. **Add a Select projection excluding Comment** at line 49-51. Project into a new `Review` object with only `Id`, `ProductId`, `CustomerName`, `Rating`, and `CreatedAt` — the same pattern already used in `Products/Detail.cshtml.cs:38` for review listings. This generates a SQL `SELECT` that omits the `Comment` column entirely.

## Expected Impact

- p95 latency: ~3-8ms reduction per reviews-by-product request (less TDS data, less materialization, less JSON serialization)
- Addresses the TDS column-reading overhead identified in the CPU hotspots profile
- Reduces per-request allocation by ~0.5-3 KB (depending on review count)
- Overall p95 improvement: ~0.5-1%
