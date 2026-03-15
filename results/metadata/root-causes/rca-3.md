# Eliminate full Reviews and Products table scans in product detail page

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:34`, the handler loads the **entire Reviews table**:

```csharp
var allReviews = await _context.Reviews.ToListAsync();
Reviews = allReviews.Where(r => r.ProductId == id)
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList();
```

At line 41, it loads the **entire Products table** just to pick 4 related products:

```csharp
var allProducts = await _context.Products.ToListAsync();
RelatedProducts = allProducts
    .Where(p => p.Category == Product.Category && p.Id != id)
    .OrderBy(_ => Guid.NewGuid())
    .Take(4)
    .ToList();
```

Additionally, the `OnPostAsync` method at line 65 loads all CartItems to check for duplicates:
```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var existing = allCartItems.FirstOrDefault(c =>
    c.SessionId == sessionId && c.ProductId == productId);
```

And at line 88, `OnPostAsync` calls `return await OnGetAsync(productId)`, re-executing all the above queries.

The k6 scenario hits this page twice per iteration: once via GET (`/Products/Detail/{id}`) and once via POST (add to cart form submission, which re-renders the page).

## Theory

Two full table scans (Reviews + Products) per GET, plus a CartItems scan on POST, means 5 full table scans across 2 requests per iteration. The Reviews table grows with the dataset, and Products (seed data) is fixed but still wasteful to load entirely when only 4 related items are needed. The `Guid.NewGuid()` ordering at line 44 prevents any query caching benefit. These materializations contribute to memory pressure and compete for DB connections with the heavier Orders and Cart operations.

## Proposed Fixes

1. **Server-side queries:** 
   - Reviews: `_context.Reviews.Where(r => r.ProductId == id).OrderByDescending(r => r.CreatedAt).ToListAsync()`
   - Related products: `_context.Products.Where(p => p.Category == Product.Category && p.Id != id).Take(4).ToListAsync()` (drop random ordering or use SQL `NEWID()` via `OrderBy(p => EF.Functions.Random())`)
   - Cart duplicate check in OnPostAsync: `_context.CartItems.FirstOrDefaultAsync(c => c.SessionId == sessionId && c.ProductId == productId)`

## Expected Impact

- p95 latency: Estimated **100-150ms overall reduction**. Two requests per iteration (~11.1% of traffic) each doing 2-3 unnecessary full table scans.
- Memory: Reduced allocations from not materializing entire Reviews and Products tables.
- DB contention: Fewer concurrent full table scans frees connection pool for heavier operations.
