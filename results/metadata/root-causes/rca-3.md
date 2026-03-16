# Add Select projection to product lookup in CreateOrder

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

At `OrdersController.cs:112-116`, `CreateOrder` loads full Product entities to build order items:

```csharp
var productIds = request.Items.Select(i => i.ProductId).ToList();
var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);
```

The only fields used from the products are `Price` (line 131: `total += product.Price * itemReq.Quantity`) and `Id` (line 120: dictionary key). Yet the query returns all columns: `Name`, `Description` (~95 chars), `Category`, `CreatedAt`, `UpdatedAt`.

Similarly at `OrdersController.cs:59-62`, `GetOrder` loads full Product entities but only uses `Name` (line 72: `ProductName = product?.Name ?? "Unknown"`):

```csharp
var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);
```

The CPU profiler's aggregate SQL data reading cost (3.8% inclusive for SqlDataReader methods) is amplified by every query that fetches unnecessary columns.

## Theory

`POST /api/orders` accounts for ~5.6% of traffic. Each call creates an order with 2 items (per `seededId` in k6), triggering a `WHERE Id IN (...)` query that returns 2 full Product entities. While the per-request savings from projecting 2 products is small, it follows the same pattern as the larger Product list endpoints — reducing unnecessary Description and metadata column reads.

Additionally, `GetOrder` (used by `CreatedAtAction` responses and potentially by direct API calls) loads full products when only `Name` is needed. Adding projection here aligns both read and write paths to fetch only what they use.

The cumulative effect across all endpoints that over-fetch Product data contributes to the 420 MB/sec allocation rate.

## Proposed Fixes

1. **Add `.Select(p => new { p.Id, p.Price })` to the CreateOrder product lookup at line 113-116**: Only `Id` and `Price` are needed for building order items and calculating totals.

2. **Add `.Select(p => new { p.Id, p.Name })` to the GetOrder product lookup at line 59-62**: Only `Id` and `Name` are needed for the response DTO.

## Expected Impact

- Per-request latency: ~1-3ms reduction on CreateOrder (eliminates Description + 4 unused columns from 2 product reads)
- Allocation: modest reduction in string allocations per order creation
- Overall p95 improvement: ~0.1-0.3% (~1ms off 544ms)
