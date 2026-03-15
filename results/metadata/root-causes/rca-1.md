# Add database indexes on frequently filtered foreign-key columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

At `AppDbContext.cs:19-63`, `OnModelCreating` defines entity configuration but **no indexes** beyond the auto-generated primary keys:

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
    // No index on SessionId
});
```

Every filtered query on these columns performs a full table scan:

- `CartItems.SessionId` — used by 6 requests/iteration: `CartController.cs:72` (`FirstOrDefaultAsync` by SessionId+ProductId), `CartController.cs:27` (join filtered by SessionId), `CartController.cs:142` (delete by SessionId), `Cart/Index.cshtml.cs:108`, `Checkout/Index.cshtml.cs:61,122`
- `Reviews.ProductId` — used by 3 requests/iteration: `ReviewsController.cs:54`, `ReviewsController.cs:66`, `Products/Detail.cshtml.cs:34`
- `OrderItems.OrderId` — used by `Orders/Index.cshtml.cs:52` (join on OrderId)
- `Orders.CustomerName` — used by `Orders/Index.cshtml.cs:41` (filter by customer)

The CPU profiler confirms SQL Server internals (`sqlmin`) dominate at **12.1% inclusive / 8.2% exclusive CPU** (~60,577 samples), with combined SQL engine components totaling ~90K samples — consistent with repeated full table scans on unindexed columns.

## Theory

Without indexes, every `WHERE SessionId = @p`, `WHERE ProductId = @p`, `WHERE OrderId = @p`, and `WHERE CustomerName = @p` clause forces SQL Server to scan the entire table row-by-row. Under 500 VUs with 10 indexed-column-dependent queries per iteration, this produces thousands of full table scans per second. Each scan holds page latches longer than an index seek would, creating contention that cascades into higher latency for all queries — including those on other tables competing for the same buffer pool and CPU.

The CartItems table grows dynamically during the test (each VU adds/removes items), so scans on CartItems.SessionId become progressively more expensive as the test continues. The Reviews table has ~2000 rows and the Orders/OrderItems tables grow with each `CreateOrder` call.

## Proposed Fixes

1. **Add HasIndex declarations in OnModelCreating** for all frequently filtered columns: `CartItem.SessionId`, a composite `(CartItem.SessionId, CartItem.ProductId)` for the AddToCart duplicate check, `Review.ProductId`, `OrderItem.OrderId`, and `Order.CustomerName`.
2. **Add raw SQL index creation in Program.cs** after `EnsureCreated()` using `IF NOT EXISTS` guards, so indexes are created even if the database already exists from a prior run.

## Expected Impact

- p95 latency: ~25ms reduction per affected request (scan → index seek), with compound benefit from reduced SQL Server contention
- RPS: moderate increase from reduced per-query cost
- Error rate: may improve if connection hold times decrease, reducing pool contention
- Overall p95 improvement: ~3.4% (55.6% of traffic × 25ms / 407ms)
