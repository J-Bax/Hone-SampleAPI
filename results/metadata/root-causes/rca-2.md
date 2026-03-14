# Replace tracked FindAsync existence checks with AnyAsync and add AsNoTracking

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

Both `GetReviewsByProduct` and `GetAverageRating` perform a tracked `FindAsync` solely to check if a product exists, materializing a full `Product` entity that is never used beyond the null check:

At lines 50-52 (`GetReviewsByProduct`):
```csharp
var product = await _context.Products.FindAsync(productId);
if (product == null)
    return NotFound(new { message = $"Product with ID {productId} not found" });
```

At lines 65-67 (`GetAverageRating`):
```csharp
var product = await _context.Products.FindAsync(productId);
if (product == null)
    return NotFound(new { message = $"Product with ID {productId} not found" });
```

Additionally, the reviews query at line 54 materializes tracked entities:
```csharp
var filtered = await _context.Reviews.Where(r => r.ProductId == productId).ToListAsync();
```

These two endpoints are each called once per VU iteration (~73 calls/sec at peak). Each `FindAsync` allocates a tracked `Product` entity (~600 bytes including strings + ~250 bytes tracking overhead) that is immediately discarded after the null check. The reviews query tracks ~4 `Review` entities per call on average (seed data: `SeedData.cs:88` creates 1-7 reviews per product across 500 products).

The memory-gc report identifies "Entity Framework change tracker bloat" as a likely allocation pattern: "Peak heap of 2GB and high Gen1 survival rate suggest tracked entities accumulating across the request lifetime." Every unnecessary tracked entity contributes to this.

## Theory

`FindAsync` loads the full entity into the change tracker's identity map, allocating an `InternalEntityEntry` with original-value snapshots. For a simple existence check, this is pure waste — `AnyAsync` translates to `SELECT CASE WHEN EXISTS(...)` in SQL, which returns a single boolean without transferring any column data or allocating entity objects.

The Product entity has string fields (Name ~30 chars, Description ~100 chars, Category ~15 chars) that each require heap allocation during materialization. Replacing `FindAsync` with `AnyAsync` eliminates all of these allocations.

For the reviews query, `AsNoTracking()` eliminates tracking overhead for the ~4 Review entities per call — modest per-request savings, but at 73 calls/sec it contributes to the aggregate allocation pressure.

## Proposed Fixes

1. **Replace `FindAsync` existence checks with `AnyAsync`:** On lines 50 and 65, replace `await _context.Products.FindAsync(productId)` with `await _context.Products.AnyAsync(p => p.Id == productId)`, and invert the null check to `if (!productExists)`.

2. **Add `.AsNoTracking()` to the reviews query:** On line 54, change to `_context.Reviews.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync()`.

## Expected Impact

- **p95 latency:** ~1% reduction from eliminating unnecessary entity materialization and tracking
- **RPS:** Marginal improvement from reduced per-request CPU work
- **Allocation rate:** ~0.5 MB/sec reduction (small but additive with other AsNoTracking changes)
- **Database load:** Reduced data transfer — `EXISTS` queries transfer fewer bytes than `SELECT * WHERE Id = @id`
