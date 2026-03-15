# Missing database indexes on high-traffic filter columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

At `AppDbContext.cs:19-63`, `OnModelCreating` defines only primary key constraints — no secondary indexes exist on any table:

```csharp
// AppDbContext.cs:58-62
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
    // No index on SessionId despite WHERE clauses in 5+ endpoints
});
```

The following high-traffic filter columns lack indexes:

- **CartItems.SessionId** — filtered in `CartController.cs:27` (GetCart), `:73` (AddToCart), `:143` (ClearCart), `Cart/Index.cshtml.cs:108` (LoadCart), `Checkout/Index.cshtml.cs:62,126` (OnPost/LoadCartSummary)
- **Reviews.ProductId** — filtered in `ReviewsController.cs:54` (GetReviewsByProduct), `:69` (GetAverageRating), `Detail.cshtml.cs:35`
- **Products.Category** — filtered in `ProductsController.cs:56` (GetProductsByCategory), `Detail.cshtml.cs:43` (related products)
- **Orders.CustomerName** — filtered in `OrdersController.cs:36` (GetOrdersByCustomer), `Orders/Index.cshtml.cs:41`
- **OrderItems.OrderId** — filtered in `OrdersController.cs:53` (GetOrder), `Orders/Index.cshtml.cs:53`

The CPU profiler confirms 55% inclusive time in `SingleQueryingEnumerable.MoveNextAsync`, showing the application is overwhelmingly bound by SQL data reading.

## Theory

Without indexes, every `WHERE` clause on these columns requires a full table scan in SQL Server. Under 500 concurrent VUs:

1. **Lock escalation**: Full table scans acquire many row locks, which SQL Server escalates to table locks under pressure, serializing concurrent access.
2. **Growing tables**: Orders and OrderItems grow continuously during the test (~73 new orders/sec). By the stress phase, the Orders table has ~8,000+ rows and OrderItems ~17,000+ rows. Full scans on these growing tables become progressively more expensive.
3. **CartItems churn**: Each VU adds and removes cart items every iteration. Without an index on SessionId, even the simple cart lookups compete for table-level I/O.

The combination of full scans + lock contention + growing tables creates compounding latency that disproportionately affects the p95 tail.

## Proposed Fixes

Add `HasIndex()` calls inside the existing `OnModelCreating` method at `AppDbContext.cs:19-63`:

1. `CartItem`: composite index on `(SessionId, ProductId)` — covers both session-filtered queries and the AddToCart upsert check
2. `Review`: index on `ProductId`
3. `Product`: index on `Category`
4. `Order`: index on `CustomerName`
5. `OrderItem`: index on `OrderId`

All changes are within the single `AppDbContext.cs` file's `OnModelCreating` method. EF Core will apply them via `EnsureCreated()` at startup.

## Expected Impact

- **p95 latency**: Estimated 20-30ms reduction on affected requests; tail latency improvement is amplified by reduced lock contention under high concurrency
- **RPS**: Higher throughput from shorter query execution times freeing DB connections faster
- **Error rate**: May improve if any errors stem from query timeouts under contention
- Index seeks replace full table scans across ~55% of all traffic, with the largest gains on the growing Orders/OrderItems tables during the stress phase
