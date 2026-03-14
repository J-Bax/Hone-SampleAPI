# Add database indexes for all high-traffic query filter columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

At `AppDbContext.cs:19-63`, the `OnModelCreating` method defines only primary keys and column constraints — **zero secondary indexes** on any table:

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
    // No index on SessionId or (SessionId, ProductId)
});
```

```csharp
modelBuilder.Entity<Review>(entity =>
{
    entity.HasKey(e => e.Id);
    // No index on ProductId
});
```

Yet these columns appear in WHERE clauses across virtually every endpoint:

- `CartController.cs:25-29` — `where ci.SessionId == sessionId` (GetCart)
- `CartController.cs:72-73` — `WHERE SessionId = ? AND ProductId = ?` (AddToCart)
- `CartController.cs:142-143` — `WHERE SessionId = ?` (ClearCart)
- `ReviewsController.cs:50` — `AnyAsync(p => p.Id == productId)` + `Where(r => r.ProductId == productId)` (lines 50-54)
- `ReviewsController.cs:65-70` — same pattern for GetAverageRating
- `ProductsController.cs:49-57` — `WHERE Category = ?` (GetProductsByCategory)
- `Pages/Orders/Index.cshtml.cs:40-44` — `WHERE CustomerName = ?` (Orders page)
- `Pages/Orders/Index.cshtml.cs:51-53` — `WHERE OrderId IN (...)` (OrderItems join)
- `Pages/Products/Detail.cshtml.cs:41-44` — `WHERE Category = ? AND Id != ?` (related products)
- `Pages/Checkout/Index.cshtml.cs:61-62` — `WHERE SessionId = ?`
- `Pages/Cart/Index.cshtml.cs:107-108` — `WHERE SessionId = ?`

The CPU profiler confirms excessive data reading: **TdsParserStateObject.TryReadChar at 4.55% inclusive** is the #1 application hotspot, and **SQL Server engine (sqlmin) at 7.6% CPU**. The memory profiler shows **779 MB/sec allocation rate** driven partly by materializing large result sets from unoptimized queries.

## Theory

Without secondary indexes, every filtered query performs a **full table scan** in SQL Server. With 1000 products, ~2000 reviews, growing CartItems (each of 500 VUs adds items per iteration), and accumulating Orders/OrderItems, the database must read every row to find matches.

Under 500 concurrent VUs executing 18 requests each (many with WHERE clauses), SQL Server performs hundreds of concurrent full table scans. This causes:
1. **High SQL CPU** (7.6%) from scanning page after page of data
2. **Lock contention** — table scans acquire shared locks on many rows, blocking concurrent writers
3. **Slow connection turnover** — queries take longer, holding connections from the pool longer, increasing pool contention
4. **Excess TDS traffic** — SQL Server reads and transmits more data pages to EF Core than needed

The CartItems table is particularly problematic: it grows continuously during the test as VUs add items. By mid-test, hundreds of rows must be scanned for every cart query.

## Proposed Fixes

Add `HasIndex()` calls in `OnModelCreating` (AppDbContext.cs) for all frequently filtered columns:

- **CartItem**: Composite index on `(SessionId, ProductId)` — covers cart add (exact match on both), get/clear (prefix match on SessionId)
- **Review**: Index on `ProductId` — covers reviews-by-product and average-rating queries
- **OrderItem**: Index on `OrderId` — covers order detail lookups and the Orders page join
- **Product**: Index on `Category` — covers category filter and related products queries
- **Order**: Index on `CustomerName` — covers the Orders page customer lookup

Since `EnsureCreated()` (Program.cs:49) won't update an existing database schema, also add a public `EnsureIndexes(AppDbContext)` method that uses `Database.ExecuteSqlRaw` with `IF NOT EXISTS` guards to create the indexes idempotently, and call it from Program.cs after `EnsureCreated()`.

## Expected Impact

- ~72% of k6 traffic (13 of 18 requests) hits filtered queries on these unindexed columns
- With proper indexes, filtered queries use index seeks (O(log N)) instead of table scans (O(N)), typically 10-100x faster
- Estimated per-request savings: 15-30ms for affected queries (higher under concurrency due to reduced lock contention)
- p95 latency: estimated 5-10% improvement
- RPS: should increase proportionally as query throughput improves
- SQL Server CPU should drop significantly (from 7.6%), freeing headroom for higher concurrency
- Error rate may improve if timeouts under stress are caused by long-running table scans
