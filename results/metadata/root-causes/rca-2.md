# Add database indexes on frequently filtered columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

In `AppDbContext.cs:19-63`, the `OnModelCreating` method defines entity configurations but **zero indexes** on any column used in WHERE clauses:

```csharp
modelBuilder.Entity<Product>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
    entity.Property(e => e.Category).HasMaxLength(100).IsRequired();
    // No index on Category — used in WHERE by ProductsController:56 and CategoriesController:41
});
```

```csharp
modelBuilder.Entity<Review>(entity =>
{
    entity.HasKey(e => e.Id);
    // No index on ProductId — used in WHERE by ReviewsController:54,71,73
});
```

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
    // No index on SessionId — used in WHERE by CartController
});
```

Similarly, `OrderItem.OrderId` (used at OrdersController:53) and `Order.CustomerName` (used at OrdersController:37) have no indexes.

The CPU profile shows TdsParserStateObject.TryReadChar at 1.7% exclusive and Unicode decoding at 1.73% — evidence that SQL Server is reading excessive data from full table scans. Even controllers already optimized with server-side `WHERE` clauses (ProductsController, ReviewsController) still force SQL Server to perform clustered index scans without proper non-clustered indexes.

With 1000 products, ~2000 reviews (SeedData.cs:86-101), and a CartItems table growing during the 120s load test with 500 VUs, these scans are expensive and repeated on every request.

## Theory

Without non-clustered indexes, every server-side `WHERE` clause translates to a SQL clustered index scan (full table read). For example, `_context.Reviews.Where(r => r.ProductId == productId)` generates `SELECT ... FROM Reviews WHERE ProductId = @p0` — without an index on `ProductId`, SQL Server scans all ~2000+ review rows to find the matching subset.

This is called 2x per k6 iteration (by-product + average rating) across 500 VUs = ~1000 full scans/second on the Reviews table alone. Similarly, `Products.Where(p => p.Category == categoryName)` scans all 1000 products per call.

The CartItems table is particularly impacted: it grows continuously as VUs add items, and every AddToCart call (still doing full table loads) gets progressively slower through the test. An index on SessionId would at least prepare for a future CartController fix.

Proper indexes convert O(n) scans to O(log n) seeks, dramatically reducing I/O, buffer pool pressure, and CPU spent in the TDS parsing layer.

## Proposed Fixes

1. **Add HasIndex calls in OnModelCreating:** Add the following index configurations in `AppDbContext.cs`:
   - `entity.HasIndex(e => e.Category)` on the Product entity (after line 28)
   - `entity.HasIndex(e => e.ProductId)` on the Review entity (after line 40)
   - `entity.HasIndex(e => e.SessionId)` on the CartItem entity (after line 61)
   - `entity.HasIndex(e => e.OrderId)` on the OrderItem entity (after line 55)
   - `entity.HasIndex(e => e.CustomerName)` on the Order entity (after line 48)

   If the application uses `EnsureCreated()`, these take effect automatically on next startup. If using EF Core migrations, a migration must also be generated.

## Expected Impact

- p95 latency: ~10-20% reduction. The 6 out of 13 baseline k6 requests that use WHERE clauses (products by-category, products search, reviews by-product, reviews average, plus cart and order operations) all benefit from index seeks instead of table scans.
- RPS: ~10-15% improvement from reduced SQL Server CPU and I/O contention — each query completes faster, freeing connections sooner.
- GC pressure: Indirect improvement — faster queries mean shorter request lifetimes and earlier object reclamation, reducing heap pressure from the current 2.7 GB peak.
- The allocation rate (~1,579 MB/sec) should decrease modestly since SQL Server returns data faster and EF Core spends less time in the materialization pipeline per query.
