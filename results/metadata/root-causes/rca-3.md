# Product detail page uses tracked queries for read-only rendering

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:30`, the product is loaded with change tracking:

```csharp
Product = await _context.Products.FindAsync(id);
```

At lines 34-37, reviews are loaded with tracking:

```csharp
Reviews = await _context.Reviews
    .Where(r => r.ProductId == id)
    .OrderByDescending(r => r.CreatedAt)
    .ToListAsync();
```

At lines 41-44, related products are loaded with tracking:

```csharp
RelatedProducts = await _context.Products
    .Where(p => p.Category == Product.Category && p.Id != id)
    .Take(4)
    .ToListAsync();
```

The `OnGetAsync` handler is read-only (no `SaveChangesAsync` calls), so all change tracking overhead — `InternalEntityEntry` construction, `NavigationFixer.InitialFixup`, `SortedDictionary` enumeration — is wasted for every request. Each request materializes ~15-25 tracked entities (1 product + 1-7 reviews + 4 related products + potentially more).

The CPU profile shows `StartTrackingFromQuery` at 2.3% inclusive with `SortedDictionary` overhead at 1.11%. While this is shared across all tracked queries, the product detail page contributes proportionally with ~15-25 entities per request at ~72 requests/sec.

## Theory

Every tracked entity allocates an `InternalEntityEntry` with snapshot data and triggers `NavigationFixer.InitialFixup` to wire up relationships. For the detail page, the reviews and related products have no navigation properties configured, making the fixup work entirely wasted. The `FindAsync` on line 30 checks the identity map first, but since `DbContext` is request-scoped and this is the first query, it always results in a tracked DB call. These tracked entities become mid-lived allocations that promote from Gen0 to Gen1, contributing to the abnormal Gen1:Gen0 ratio (0.94) and the 89.4ms max Gen1 pauses observed in the GC profile.

## Proposed Fixes

1. **Replace `FindAsync` with `AsNoTracking().FirstOrDefaultAsync`:** Change line 30 to `await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id)`. Add `.AsNoTracking()` before `.Where()` on lines 34 and 41. This eliminates all tracking overhead for the read-only page render.

## Expected Impact

- p95 latency: ~5-10ms reduction per request through eliminated tracking of ~15-25 entities and reduced Gen1 pressure
- Memory: Modest reduction in allocation rate, complementing the Products page fix
- Overall: ~0.5-1% p95 improvement; combined with opportunity #1, the cumulative AsNoTracking benefit across both pages is ~3-4%
