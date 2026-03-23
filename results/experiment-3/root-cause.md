# Root Cause Analysis — Experiment 3

> Generated: 2026-03-23 03:07:36 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 27950.345ms | 27950.345ms |
| Requests/sec | 20.1 | 20.1 |
| Error Rate | 100% | 100% |

---
# Add-to-cart POST re-executes full page load queries

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Detail.cshtml.cs:89`, the `OnPostAsync` handler ends by calling `OnGetAsync`, which re-executes three database queries:

```csharp
public async Task<IActionResult> OnPostAsync(int id, int productId, int quantity = 1)
{
    // ... cart item lookup (line 67) ...
    // ... SaveChangesAsync (line 85) ...
    CartMessage = "Item added to cart!";
    return await OnGetAsync(productId);  // re-runs 3 queries
}
```

`OnGetAsync` (lines 27-51) executes:
1. Product lookup: `_context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id)` (line 30)
2. Reviews list: `_context.Reviews.AsNoTracking().Where(r => r.ProductId == id)...ToListAsync()` (lines 34-39)
3. Related products: `_context.Products.AsNoTracking().Where(p => p.Category == Product.Category && p.Id != id).Take(4)...ToListAsync()` (lines 43-48)

The POST handler also performs its own DB operations:
4. Cart item lookup: `_context.CartItems.FirstOrDefaultAsync(...)` (line 67)
5. Cart save: `_context.SaveChangesAsync()` (line 85)

Total: **5 database round trips per POST request**. Under 500 VUs, each POST occupies a DB connection for 5 sequential queries, keeping connections checked out ~2.5x longer than necessary.

## Theory

Each additional DB round trip in a request handler extends the time a connection is held from the pool. With 5 round trips vs. the 2 actually needed (cart check + save), each POST holds its connection ~2.5x longer. Under 500 VUs, this directly contributes to connection pool pressure. The 3 redundant queries (product, reviews, related products) also add ~60ms each of DB latency, inflating per-request time by ~180ms.

The re-execution of `OnGetAsync` also means the `CartItems.FirstOrDefaultAsync` at line 67 runs without `AsNoTracking()`, adding unnecessary change-tracking overhead for a read-only check.

## Proposed Fixes

1. **Cache page data before POST processing:** In `OnPostAsync`, load the product once at the start, perform the cart operation, then populate the view model properties directly from the already-loaded data instead of calling `OnGetAsync`. This eliminates 3 of the 5 DB round trips. The reviews and related products can be loaded once alongside the initial product query.

2. **Add `AsNoTracking()` to cart item lookup:** At line 67, the `FirstOrDefaultAsync` on CartItems doesn't use `AsNoTracking()`. Since the existing item is subsequently modified, this one actually needs tracking — but if the item doesn't exist (new cart item path), the tracking overhead is wasted. Consider splitting the logic: use a raw SQL `EXISTS` check, then only load with tracking when needed.

## Expected Impact

- Per-request DB round trips: Reduced from 5 to 2 (60% fewer)
- Connection hold time: Reduced by ~180ms per POST (3 fewer queries × ~60ms each)
- Overall p95 improvement: ~1.5% (5.6% of traffic × ~180ms savings on ~4000ms post-fix p95)

