# Add Select projection to FeaturedProducts and RecentReviews excluding large text fields

> **File:** `SampleApi/Pages/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:28-31`, the Home page loads 12 featured products as full entities including the `Description` field:

```csharp
FeaturedProducts = await _context.Products.AsNoTracking()
    .OrderBy(_ => EF.Functions.Random())
    .Take(12)
    .ToListAsync();
```

At `Pages/Index.cshtml.cs:38-41`, it also loads 5 recent reviews as full entities including the `Comment` field (up to 2000 chars per `Models/Review.cs:12`):

```csharp
RecentReviews = await _context.Reviews.AsNoTracking()
    .OrderByDescending(r => r.CreatedAt)
    .Take(5)
    .ToListAsync();
```

Neither query uses a Select projection. The `Description` field averages ~100 chars across 12 products (~1.2KB), and the `Comment` field can be up to 2000 chars across 5 reviews (up to ~10KB). The CPU profile shows Unicode string decoding at 1.5% exclusive CPU — directly proportional to string column volume.

## Theory

The Home page is rendered on every k6 iteration. The `EF.Functions.Random()` ordering already forces a full table scan of 1000 products (sorted randomly, top 12 returned). The full entity materialization adds unnecessary string allocation for Description fields that a home page product card typically doesn't display. The RecentReviews query fetches full Comment text; a home page review summary only needs rating, customer name, and a truncated preview. Together, these queries allocate ~1.2KB (products) + up to ~10KB (reviews) of unnecessary string data per request, flowing through the SQL TDS parser → Unicode decoder → EF materializer → Razor serializer pipeline.

## Proposed Fixes

1. **Add Select projection to FeaturedProducts:** At line 28-31, add `.Select(p => new Product { Id = p.Id, Name = p.Name, Price = p.Price, Category = p.Category, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt })` before `.ToListAsync()`, matching the pattern in `ProductsController`. This excludes Description from the SQL query.

2. **Add Select projection to RecentReviews:** At lines 38-41, add `.Select(r => new Review { Id = r.Id, ProductId = r.ProductId, CustomerName = r.CustomerName, Rating = r.Rating, CreatedAt = r.CreatedAt })` to exclude Comment from the SQL query. If a truncated comment preview is needed on the page, include a substring projection.

## Expected Impact

- p95 latency: ~3-8ms reduction on Home page requests (less SQL data, fewer string allocations)
- GC pressure: reduced allocation volume from eliminating up to ~11KB of unnecessary string data per request
- The Home page is ~5.6% of total k6 traffic. With ~5ms average savings, overall p95 improvement is approximately 0.05%.
