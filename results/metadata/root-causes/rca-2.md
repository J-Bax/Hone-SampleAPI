# No database indexes on frequently filtered columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

At `Data/AppDbContext.cs:19-63`, the `OnModelCreating` method configures entity properties (max lengths, column types) but defines **zero indexes** beyond auto-created primary keys:

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
});
```

Since the models use raw integer IDs (no navigation properties or `.HasForeignKey()` calls), EF Core does **not** auto-create foreign key indexes. Every `WHERE` clause on these columns results in a full table scan at the database level:

- `CartItems.SessionId` — queried by every cart operation (API `CartController.cs:25-29`, `CartController.cs:72-73`, `CartController.cs:142-143`; Pages `Cart/Index.cshtml.cs:107`, `Checkout/Index.cshtml.cs:61`, `Checkout/Index.cshtml.cs:125`)
- `Reviews.ProductId` — queried by `ReviewsController.cs:54`, `ReviewsController.cs:69`, `Products/Detail.cshtml.cs:34`
- `OrderItems.OrderId` — queried by `OrdersController.cs:52`, `Orders/Index.cshtml.cs:51-53`
- `Products.Category` — queried by `ProductsController.cs:55-56`, `Products/Detail.cshtml.cs:41`
- `Orders.CustomerName` — queried by `OrdersController.cs:36`, `Orders/Index.cshtml.cs:41`

The CPU profiler shows 10.3% of CPU in SQL data access aggregate (`SqlDataReader.ReadAsync`, `TdsParser.TryRun`, etc.), confirming the database is doing excessive work reading data.

## Theory

Without indexes, SQL Server performs full table scans for every filtered query. With 1,000 products, ~2,000 reviews, 100+ orders (growing rapidly as k6 creates new ones), and a CartItems table that grows throughout the test, these scans become increasingly expensive. Under high concurrency (500 VUs), table scans also cause increased lock contention — shared table locks from concurrent SELECT scans block INSERT/UPDATE operations on the same tables, creating artificial serialization points. This is particularly acute for CartItems, which is simultaneously read and written by every VU iteration (add-to-cart, get-cart, clear-cart, checkout). Adding indexes converts O(n) scans to O(log n) seeks and dramatically reduces lock scope from table-level to row-level.

## Proposed Fixes

1. **Add indexes in OnModelCreating:** Add `.HasIndex()` calls for the most frequently queried columns:
   - `CartItems`: composite index on `(SessionId, ProductId)` and single on `SessionId`
   - `Reviews`: index on `ProductId`
   - `OrderItems`: index on `OrderId`
   - `Products`: index on `Category`
   - `Orders`: index on `CustomerName`

   Since the app uses `EnsureCreated()` (not migrations), the indexes will be created when the database is next provisioned.

## Expected Impact

- p95 latency: ~5-15ms reduction per affected request (index seek vs table scan), compounding under concurrency due to reduced lock contention
- RPS: improved throughput from reduced DB lock contention and I/O
- Error rate: may decrease slightly if some errors are caused by DB timeout under contention
- ~67% of requests (12/18 per iteration) hit queries on non-PK columns that would benefit from indexes
