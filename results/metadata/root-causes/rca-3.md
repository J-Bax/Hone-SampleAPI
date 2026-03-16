# Add Select projection to product name lookup in Orders page

> **File:** `SampleApi/Pages/Orders/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Orders/Index.cshtml.cs:58-61`, the Orders page fetches full `Product` entities but only uses the `Name` property:

```csharp
var productMap = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);   // Full Product entities
```

The only property accessed is `Name` (line 69):
```csharp
ProductName = product?.Name ?? "Unknown",
```

All other columns — `Description`, `Price`, `Category`, `CreatedAt`, `UpdatedAt` — are fetched from SQL Server, materialized into .NET objects, and immediately discarded. This is the same anti-pattern already fixed in `Checkout/Index.cshtml.cs:136-137` with a Select projection.

The CPU profiler's top hotspot is `TdsParserStateObject.TryReadChar` at 1.1% exclusive — reading unnecessary Unicode string data. The `StringConverter.Write` at 0.56% and aggregate JSON serialization at 1.4% don't apply here (Razor page, not JSON), but the SQL read overhead does.

## Theory

The Orders page is the last request in every VU iteration, occurring at peak load (up to 500 VUs). Each request loads 1-N orders, their items, and then full Product entities for all referenced products. The Product `Description` field (nvarchar potentially up to 2000+ chars) dominates the unnecessary data transfer. Under the k6 scenario, each VU creates orders with 2 items per iteration, and the orders page loads ALL orders for that customer across all iterations. As the test progresses, the number of orders per customer grows, amplifying the unnecessary product data fetched. This contributes to the 422 MB/sec allocation rate and Gen0→Gen1 promotion crisis identified by the GC profiler.

## Proposed Fixes

1. **Add Select projection for Name only:** Replace the full entity query with `.Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id)`. Update the `TryGetValue` on line 69 to use the anonymous type's `Name` property. This eliminates 5 unused columns from the SQL query.

## Expected Impact

- p95 latency: ~2-3ms reduction per orders page request
- GC pressure: meaningful reduction in mid-life string allocations
- This endpoint is ~5.5% of traffic. Overall p95 improvement: ~0.2%.
