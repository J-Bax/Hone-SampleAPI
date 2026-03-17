# Root Cause Analysis — Experiment 45

> Generated: 2026-03-17 00:31:12 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 505.0618ms | 7546.103045ms |
| Requests/sec | 1244.7 | 125.5 |
| Error Rate | 0% | 0% |

---
# Combine two-query cart+product pattern into single join in Checkout page

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Checkout/Index.cshtml.cs:125-138`, `LoadCartSummary` makes two separate DB round trips:

```csharp
var sessionItems = await _context.CartItems
    .AsNoTracking()
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();  // Query 1: cart items

var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .Select(p => new { p.Id, p.Name, p.Price })
    .ToDictionaryAsync(p => p.Id);  // Query 2: products
```

The same two-query pattern appears in `OnPostAsync` at lines 61-88, where cart items and products are loaded separately before creating order items.

The CPU profiler shows SQL data reading at 8.2% inclusive and EF Core enumeration at 10.5% — each extra DB round trip adds connection pool contention under 500 concurrent VUs. The memory profiler identified a 293 MB/s allocation rate; the intermediate `List<CartItem>` and `Dictionary<int, anonymous>` allocations contribute to the mid-life crisis pattern where 85% of Gen0 objects promote to Gen1.

## Theory

Under high concurrency (up to 500 VUs), each additional DB round trip holds a connection pool slot longer and increases queuing delay for other requests. The two-query pattern materializes an intermediate `List<CartItem>`, extracts product IDs, then makes a second query — doubling the connection hold time and creating intermediate allocations. A single join sends one SQL statement to the server, halving the connection hold time and eliminating the intermediate materialization.

This exact pattern was successfully optimized in `CartController.GetCart` during experiment 44 (RPS improved from 1196.8 to 1244.7), confirming the approach works.

## Proposed Fixes

1. **Single join in LoadCartSummary:** Replace the two separate queries with a single LINQ `Join` between `CartItems` and `Products`, projecting directly into the fields needed for `CartItemView` (`ProductId`, `ProductName`, `ProductPrice`, `Quantity`). This eliminates one DB round trip and the intermediate `List<CartItem>` + `Dictionary` allocations.

2. **Single join in OnPostAsync:** Similarly replace the cart items + products two-query pattern (lines 61-88) with a single join that returns `ProductId`, `Quantity`, and `Price` — all the data needed to create `OrderItem` entries and compute the total.

## Expected Impact

- p95 latency: ~5-10ms reduction on affected requests (eliminating 1 DB round trip each)
- The compound effect under 500 VUs (reduced connection pool hold time) amplifies the per-request savings
- Fewer intermediate allocations (no List + Dictionary) reduces Gen1 GC pressure
- Overall p95 improvement: ~1-2%

