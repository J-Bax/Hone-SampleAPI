# Add Select projection to product dictionary lookup in GetCart API endpoint

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:30-34`, the `GetCart` endpoint loads full Product entities:

```csharp
var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);
```

This materializes complete `Product` objects including `Description` (up to ~100 chars of NVARCHAR), `Category`, `CreatedAt`, and `UpdatedAt` — none of which are used. The only fields consumed are `Name` (line 49) and `Price` (lines 42, 50):

```csharp
ProductName = product?.Name ?? "Unknown",
ProductPrice = product?.Price ?? 0m,
```

The same optimization was already successfully applied to:
- Cart page `LoadCart` (experiment 22, `Pages/Cart/Index.cshtml.cs:119-122`)
- Orders page product lookup (experiment 23, `Pages/Orders/Index.cshtml.cs:58-62`)

But the API controller endpoint was missed.

The CPU profile shows 4.08% of samples in TDS parsing (materializing SQL data into .NET objects) and 0.51% in Unicode string decoding — both inflated by reading unnecessary NVARCHAR columns.

## Theory

Every VU iteration calls `GET /api/cart/{sessionId}`, which triggers this product lookup. While each cart typically has only 1 item (the k6 scenario adds 1 then clears), under high concurrency (500 VUs) this means 500+ concurrent queries materializing full Product rows. The extra columns (especially `Description` as NVARCHAR) consume:
- SQL Server I/O to read wider rows
- TDS network bandwidth for unused data
- .NET heap allocations for string materialization
- JSON serialization overhead (though unused fields aren't in the response)

Using `.Select()` to project only `Id`, `Name`, and `Price` reduces the SQL result width by ~60%, cutting materialization and allocation overhead.

## Proposed Fixes

1. **Add Select projection:** At `CartController.cs:30-34`, change the product query to project only the needed columns:
   ```
   .Select(p => new { p.Id, p.Name, p.Price })
   .ToDictionaryAsync(p => p.Id);
   ```
   This mirrors the pattern already used in `Pages/Cart/Index.cshtml.cs:121` and `Pages/Orders/Index.cshtml.cs:61`.

## Expected Impact

- Reduces SQL data transfer for product lookups in cart API by ~60%
- Reduces .NET heap allocations per request (fewer string materializations)
- Expected per-request latency reduction: ~5-8 ms
- Overall p95 improvement: ~0.1% (cart API is ~5.5% of traffic, small per-request gain)
