# No database indexes on frequently filtered columns causes full table scans

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

In `AppDbContext.cs:19-63`, the `OnModelCreating` method defines primary keys and property constraints but zero indexes. The most frequently queried filter columns have no index:

- `Review.ProductId` — filtered by `GetReviewsByProduct` and `GetAverageRating` (lines 37-42 define Review entity with no `.HasIndex()`)
- `CartItem.SessionId` — filtered by `GetCart`, `AddToCart`, `ClearCart` (lines 58-62 define CartItem with no `.HasIndex()`)
- `OrderItem.OrderId` — filtered by `GetOrder` (lines 52-55 define OrderItem with no `.HasIndex()`)
- `Product.Category` — filtered by `GetProductsByCategory` (lines 23-29 define Product with no `.HasIndex()`)

Once client-side evaluation is fixed (opportunities #1 and #2), these WHERE clauses will execute server-side. Without indexes, SQL Server must perform full table scans on every filtered query. With 2000+ reviews, 1000 products, and growing OrderItems/CartItems tables under load, scan cost compounds.

## Theory

After fixing client-side evaluation, the SQL queries will push `WHERE ProductId = @p0`, `WHERE SessionId = @p0`, etc. to the database. Without non-clustered indexes on these columns, SQL Server uses clustered index scans (sequential reads of every row) instead of index seeks (direct lookups). For the Reviews table at 2000+ rows queried twice per VU iteration, and CartItems table growing continuously during the test, full scans create I/O contention under high concurrency. The CartItem.SessionId index is particularly important because cart operations (`AddToCart` at CartController.cs:65-66 uses `FirstOrDefaultAsync` with SessionId + ProductId filter) occur on every VU iteration.

This is classified as `architecture` because adding indexes requires an EF Core migration (`dotnet ef migrations add ...`), which generates additional migration files.

## Proposed Fixes

1. **Add indexes in OnModelCreating:** Add `.HasIndex(e => e.ProductId)` to the Review entity, `.HasIndex(e => e.SessionId)` to CartItem, `.HasIndex(e => e.OrderId)` to OrderItem, and `.HasIndex(e => e.Category)` to Product. Then generate and apply a migration.

2. **Consider composite index for CartItem:** Add `.HasIndex(e => new { e.SessionId, e.ProductId })` since `AddToCart` filters on both columns together (CartController.cs:65-66).

## Expected Impact

- **p95 latency:** Expected 5-15% additional reduction after client-side evaluation fixes are applied, as index seeks replace table scans.
- **RPS:** Expected 5-10% throughput improvement from reduced I/O wait times and lock contention on table scans.
- **Scalability:** Indexes prevent query degradation as tables grow during extended load tests (CartItems and Orders grow with each VU iteration).
