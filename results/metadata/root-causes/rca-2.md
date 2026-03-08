# Missing database indexes on frequently-filtered columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

In `AppDbContext.cs:19-63`, the `OnModelCreating` method defines entity configuration but no indexes. The following columns are used in `Where` clauses across multiple controllers but have no database index:

- `Order.CustomerName` ΓÇö filtered at `OrdersController.cs:37` in `GetOrdersByCustomer`
- `OrderItem.OrderId` ΓÇö filtered at `OrdersController.cs:53` in `GetOrder`
- `Review.ProductId` ΓÇö filtered at `ReviewsController.cs:54` and `ReviewsController.cs:69`
- `CartItem.SessionId` ΓÇö filtered at `CartController.cs:26` and `CartController.cs:136`
- `Product.Category` ΓÇö filtered at `ProductsController.cs:56` and `CategoriesController.cs:41`

For example, the CartItem entity at `AppDbContext.cs:58-62`:

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
});
```

No `HasIndex` is configured on `SessionId`, despite it being the primary lookup key for cart operations.

## Theory

Without indexes, every `Where` clause on these columns results in a full table scan at the SQL level. With 1000 products, ~2000 reviews, 100 orders, and ~250 order items, the tables are small enough that the impact is moderate ΓÇö but under concurrent k6 load, the cumulative cost of repeated full scans causes lock contention and increased I/O wait times. The `Product.Category` and `Review.ProductId` columns are particularly impactful because those tables are the largest (1000 and ~2000 rows respectively).

This is classified as `architecture` because adding indexes requires an EF Core migration.

## Proposed Fixes

1. **Add HasIndex calls in OnModelCreating:** In `AppDbContext.cs`, add index configuration for each entity:
   - `entity.HasIndex(e => e.CustomerName)` for Order (line ~49)
   - `entity.HasIndex(e => e.OrderId)` for OrderItem (line ~55)
   - `entity.HasIndex(e => e.ProductId)` for Review (line ~40)
   - `entity.HasIndex(e => e.SessionId)` for CartItem (line ~61)
   - `entity.HasIndex(e => e.Category)` for Product (line ~28)

2. **Generate and apply migration:** Run `dotnet ef migrations add AddPerformanceIndexes` and `dotnet ef database update`.

## Expected Impact

- p95 latency: ~10-15% reduction. Index seeks replace full table scans, particularly noticeable on the larger Products and Reviews tables.
- RPS: ~10% increase. Less time holding locks during scans means higher concurrency.
- Error rate: No change.
