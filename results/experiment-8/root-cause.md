# Root Cause Analysis — Experiment 8

> Generated: 2026-03-10 02:07:48 | Classification: narrow — Adding indexes via DbContext.OnModelCreating is a single-file DbContext configuration change that modifies only implementation internals without requiring new migration files or changes to the public API.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 409.68509ms | 888.549155000001ms |
| Requests/sec | 1335.7 | 683.2 |
| Error Rate | 0% | 0% |

---
# Add database indexes on frequently filtered columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** narrow

## Evidence

In `AppDbContext.cs:19-63`, `OnModelCreating` defines entity keys and property constraints but **zero secondary indexes**:

```csharp
modelBuilder.Entity<Review>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.CustomerName).HasMaxLength(100).IsRequired();
    entity.Property(e => e.Comment).HasMaxLength(2000);
});
```

Yet multiple hot-path endpoints filter on non-key columns with no index support:

- `ReviewsController.cs:54-56` — `WHERE ProductId = {id}` on ~2000 Reviews, called twice per VU iteration (by-product + average)
- `ProductsController.cs:55-57` — `WHERE Category = 'Electronics'` on 1000 Products, called once per iteration
- `Detail.cshtml.cs:34-37` — `WHERE ProductId = {id}` on Reviews, once per iteration
- `Detail.cshtml.cs:41-43` — `WHERE Category = {cat}` on Products, once per iteration
- `OrdersController.cs:53-56` — `WHERE OrderId = {id}` on OrderItems
- `CartItems` has no index on `SessionId` or `(SessionId, ProductId)`, forcing full scans on every cart query

The CPU hotspot profile confirms this: `SingleQueryingEnumerable.MoveNextAsync` at 22.5% inclusive, `TdsParser.TryRun` at 8.5%, and `SqlDataReader.TryReadColumnInternal` at 5.34% — dominated by SQL Server processing unnecessary rows.

## Theory

Without indexes, every `WHERE` clause triggers a **full table scan** in SQL Server. On the Reviews table (2000+ rows growing with writes), each `WHERE ProductId = X` reads every row. On Products (1000 rows), each `WHERE Category = X` reads every row. Under load (500 VUs × ~6 filtered queries/iteration), SQL Server executes thousands of full scans per second. The 17% CPU in `sqlmin/sqllang` from the profile is largely index-seek-avoidable table scan work. CartItems grows continuously during the test (each VU adds items), making its full scans progressively worse.

## Proposed Fixes

1. **Add `HasIndex` calls in `OnModelCreating`** at `AppDbContext.cs:37-42` (Reviews), `AppDbContext.cs:23-29` (Products), `AppDbContext.cs:52-56` (OrderItems), and `AppDbContext.cs:58-62` (CartItems):
   - `Reviews.ProductId` — covers both by-product and average endpoints
   - `Products.Category` — covers by-category, search-filtered, and related-products queries
   - `OrderItems.OrderId` — covers order detail item lookup
   - `CartItems.SessionId` — covers session-scoped cart queries
   - `CartItems(SessionId, ProductId)` composite — covers add-to-cart duplicate check

   EF Core will generate these as `CREATE INDEX` in the next migration.

## Expected Impact

- **p95 latency**: −30 to 60ms (7–15% reduction from 409ms). Each indexed query saves 1–5ms by replacing table scan with index seek; ~6 filtered queries per iteration compound.
- **RPS**: +5–10% improvement. SQL Server spends less CPU on scans, freeing capacity for more concurrent requests.
- **Allocation rate**: Slight reduction — SQL Server returns result sets faster, reducing connection hold time and async state machine lifetimes.
- **Growing CartItems table**: Index prevents the progressive slowdown that occurs as more items are inserted during the test.

