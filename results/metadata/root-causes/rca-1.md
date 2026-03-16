# Add database indexes on high-traffic filter columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

At `AppDbContext.cs:19-63`, `OnModelCreating` defines entity configurations with **primary keys only** — no secondary indexes on any filter or foreign-key column:

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
});
```

Every `WHERE` clause on these columns forces SQL Server into a full table scan:
- `CartItems.SessionId` — used in **6 endpoints** (AddToCart duplicate check, GetCart, ClearCart, Cart page, Checkout GET, Checkout POST)
- `Reviews.ProductId` — used in **4 endpoints** (GetReviewsByProduct, GetAverageRating, Detail page GET, Detail page POST)
- `Products.Category` — used in **2 endpoints** (GetProductsByCategory, Detail page related products)
- `Orders.CustomerName` — used in **1 endpoint** (Orders page)
- `OrderItems.OrderId` — used in **1 endpoint** (Orders page)

The CPU profile shows **19.5% of CPU in TDS parsing** (TryReadChar, TryReadSqlStringValue, TryReadPlpUnicodeCharsChunk), confirming SQL Server reads far more rows than needed.

During the load test, the CartItems table grows rapidly (every VU iteration adds items), so scans become progressively more expensive. With 500 VUs and 1000 seed products + 2000 reviews, unindexed scans under concurrency cause page-level lock contention that directly inflates p95 latency.

## Theory

Without indexes, every filtered query does a sequential table scan — reading and locking every row in the table to find matching records. Under 500-VU concurrency, hundreds of concurrent scans compete for the same data pages, creating lock contention and queuing. This is amplified for CartItems because the table grows throughout the test (each VU adds/removes items), making scans increasingly expensive as the load test progresses.

The TDS parsing dominance (19.5% CPU) is a direct symptom: SQL Server sends many irrelevant rows across the wire because it scans entire tables instead of seeking to matching rows via an index. Adding indexes converts O(n) scans to O(log n) seeks, dramatically reducing both CPU (less data parsed) and lock contention (shorter lock hold times).

## Proposed Fixes

1. **Add indexes in `OnModelCreating`:** Add `.HasIndex()` calls for each high-traffic filter column:
   - `CartItem`: index on `SessionId`, plus a composite unique-ish index on `(SessionId, ProductId)` for the AddToCart duplicate check (`CartController.cs:77-78`, `Detail.cshtml.cs:63-64`)
   - `Review`: index on `ProductId`
   - `Product`: index on `Category`
   - `Order`: index on `CustomerName`
   - `OrderItem`: index on `OrderId`

All changes are in `AppDbContext.cs` `OnModelCreating`. A new EF migration will be needed to apply the schema change.

## Expected Impact

- **p95 latency**: estimated 25-40ms reduction. Under high concurrency, converting table scans to index seeks reduces lock contention significantly, which disproportionately benefits p95 (tail latency).
- **RPS**: modest increase from reduced per-request DB time.
- **CPU**: TDS parsing percentage should drop as SQL Server returns fewer rows per query.
- This is the single highest-impact change because it affects ~67% of all traffic and addresses the root cause of the dominant CPU hotspot (TDS parsing).
