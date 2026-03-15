# Skip redundant product existence check when reviews are found

> **File:** `SampleApi/Controllers/ReviewsController.cs` | **Scope:** narrow

## Evidence

At `Controllers/ReviewsController.cs:50–54`, the `GetReviewsByProduct` endpoint always executes two sequential database queries:

```csharp
var productExists = await _context.Products.AnyAsync(p => p.Id == productId);
if (!productExists)
    return NotFound(new { message = $"Product with ID {productId} not found" });

var filtered = await _context.Reviews.AsNoTracking()
    .Where(r => r.ProductId == productId).ToListAsync();
```

The `AnyAsync` product existence check runs unconditionally on every request, even though finding reviews for a product ID implicitly proves the product exists.

## Theory

The k6 scenario calls this endpoint with `seededId(500, 2)`, generating product IDs in the range [1, 500]. Per `SeedData.cs:86–88`, all products 1–500 have 1–7 reviews each. So for ~100% of k6 traffic to this endpoint, the reviews query returns a non-empty list, making the prior existence check a wasted DB round trip.

The existence check is only meaningful when the reviews list is empty — to distinguish "product exists but has no reviews" (return empty 200) from "product doesn't exist" (return 404). By reversing the query order, we eliminate the extra round trip in the overwhelmingly common case while preserving correct 404 behavior.

## Proposed Fixes

1. **Fetch reviews first, check existence only when empty:** Execute the reviews query first. If the result list is non-empty, return it immediately (the product clearly exists). If the list is empty, then run `AnyAsync` to determine whether to return an empty 200 or a 404. This preserves the exact same API behavior while saving one DB round trip for the common case.

## Expected Impact

- p95 latency: ~5ms reduction per request for the common case (1 fewer DB round trip)
- The existence check still runs for the rare empty-reviews case, preserving 404 correctness
- Clean, low-risk change with no behavioral difference
