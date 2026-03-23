# Missing database index on OrderItem.ProductId causes slow joins for order endpoints

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** narrow

## Evidence

At `AppDbContext.cs:55-60`, the `OrderItem` entity configuration only indexes `OrderId`:

```csharp
modelBuilder.Entity<OrderItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.UnitPrice).HasColumnType("decimal(18,2)");
    entity.HasIndex(e => e.OrderId);
});
```

There is **no index on `OrderItem.ProductId`**. Multiple endpoints join `OrderItems` with `Products` on `ProductId`:

- `OrdersController.GetOrder` (lines 58-63): `_context.Products.Where(p => productIds.Contains(p.Id))`
- `OrdersController.CreateOrder` (lines 112-117): `_context.Products.Where(p => productIds.Contains(p.Id))`
- `Orders/Index.cshtml.cs` (lines 56-62): `_context.Products.Where(p => productIds.Contains(p.Id))`

The `Orders` page is hit once per VU iteration (5.6% of traffic), and `POST /api/orders` (CreateOrder) is also hit once per VU iteration (another 5.6%). Together, these account for ~11.2% of traffic doing product lookups by ID for order items.

Additionally, the `Order` entity has an index on `CustomerName` (line 52) but no index on `Status`, which is queried in the seed data but not in the k6 scenario. The important missing index is `OrderItem.ProductId`.

## Theory

Without an index on `OrderItem.ProductId`, the `Contains(p.Id)` queries that join order items with products require scanning the OrderItems table. With ~100 seed orders having 1-5 items each (~300 order items initially), plus continuous order creation under load (500 VUs each creating an order per iteration), the OrderItems table grows rapidly during the test. Each product-ID lookup becomes progressively slower as the table grows.

Under 500 concurrent VUs, each creating orders and viewing order history, the unindexed scans compound connection hold times. Combined with the other endpoints' connection pressure, this contributes to pool exhaustion.

## Proposed Fixes

1. **Add an index on `OrderItem.ProductId`:** In the `OnModelCreating` method, add `entity.HasIndex(e => e.ProductId)` to the `OrderItem` configuration block (after line 59). This enables index seeks instead of table scans for all product-ID lookups on order items.

## Expected Impact

- Product lookup queries in order endpoints change from table scans to index seeks
- Per-request latency for order endpoints reduced by ~10-30ms as OrderItems table grows
- p95 latency improvement: ~2-3% overall (11.2% combined traffic × modest per-query improvement)
- Secondary benefit: faster queries release connections sooner, reducing pool contention
