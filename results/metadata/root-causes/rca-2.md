# Combine LoadCart two-query pattern into single join query

> **File:** `SampleApi/Pages/Cart/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Cart/Index.cshtml.cs:107-122`, `LoadCart` makes two separate DB queries:

```csharp
var sessionItems = await _context.CartItems
    .AsNoTracking()
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();  // Query 1: cart items

var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .Select(p => new { p.Id, p.Name, p.Price })
    .ToDictionaryAsync(p => p.Id);  // Query 2: product lookup
```

This is the identical two-query pattern that was successfully optimized in `CartController.GetCart` during experiment 44, which improved RPS from 1196.8 to 1244.7.

The memory profiler shows 293 MB/s allocation rate (~235KB per request). The intermediate `List<CartItem>` (full entity materialization) plus `Dictionary<int, anonymous>` allocations contribute to GC pressure, with Gen1 pauses reaching 139.5ms.

## Theory

Each GET /Cart request holds a DB connection for two sequential queries. Under 500 concurrent VUs competing for the SQL Server connection pool (default 100 connections), this doubles the queuing delay. The intermediate `List<CartItem>` materializes full entities (including `SessionId`, `AddedAt` fields not needed downstream) before extracting just the product IDs, wasting memory. A single join sends one SQL statement to the server and materializes only the projected fields needed for `CartItemViewModel`.

## Proposed Fixes

1. **Single LINQ Join in LoadCart:** Replace the two queries with `_context.CartItems.AsNoTracking().Where(c => c.SessionId == sessionId).Join(_context.Products, c => c.ProductId, p => p.Id, (c, p) => new { c.Id, c.ProductId, ProductName = p.Name, ProductPrice = p.Price, c.Quantity })`. Then iterate the single result list to build `CartItemViewModel` objects and compute totals. This matches the pattern already applied to `CartController.GetCart` in experiment 44.

## Expected Impact

- p95 latency: ~5-10ms reduction per cart page request
- Eliminates intermediate allocations (List + Dictionary), reducing Gen1 GC pressure
- Overall p95 improvement: ~0.5-1%
