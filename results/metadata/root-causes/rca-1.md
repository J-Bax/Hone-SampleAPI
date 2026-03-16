# Add database indexes for frequently filtered columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

The `OnModelCreating` method in `AppDbContext.cs` (lines 19–63) configures entity primary keys and property constraints but defines **zero indexes on non-PK columns**. Meanwhile, virtually every query in the application filters on these unindexed columns:

- `CartItems.Where(c => c.SessionId == sessionId)` — used in CartController:25, Cart page:107, Checkout page:61/126
- `Reviews.Where(r => r.ProductId == productId)` — used in ReviewsController:54, Detail page:34
- `Products.Where(p => p.Category == categoryName)` — used in ProductsController:58, Detail page:41
- `OrderItems.Where(i => orderIds.Contains(i.OrderId))` — used in Orders page:51
- `Orders.Where(o => o.CustomerName == customer)` — used in Orders page:42

For example, the CartItem entity configuration (lines 58–62) only sets a PK and a property constraint:

```csharp
modelBuilder.Entity<CartItem>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
});
```

No `HasIndex(e => e.SessionId)` or similar. The CPU profiling report confirms: *"The CPU profile is dominated by SQL data reading (~30% inclusive from EF Core through SqlClient TDS parsing, Unicode decoding, and string materialization), indicating the API fetches too many rows and/or columns per request."*

## Theory

Without indexes on filter columns, every `WHERE` clause forces SQL Server to perform a **full table scan** — reading every row and evaluating the predicate row by row. Under 500 concurrent VUs this creates three compounding problems:

1. **Lock contention**: Table scans acquire shared locks on all data pages. With hundreds of VUs scanning the same table simultaneously, latch contention on data pages serializes queries and inflates wait times.
2. **Growing tables**: The `CartItems` and `Orders` tables grow continuously during the load test (each VU creates cart items and orders per iteration). Without indexes, scan cost increases proportionally with table size, making requests progressively slower — disproportionately inflating **p95 tail latency** toward the end of the test.
3. **Connection pool pressure**: Longer scan times mean each query holds a DB connection longer, increasing pool exhaustion and queuing delays for all other requests.

This affects ~67% of total traffic (12 of 18 requests per k6 iteration filter on at least one unindexed column).

## Proposed Fixes

1. **Add `HasIndex` definitions in `OnModelCreating`** for the five most critical columns:
   - `CartItems.SessionId` — used by 5 endpoints (~28% of traffic). Also add a composite index on `(SessionId, ProductId)` for the AddToCart duplicate-check query (CartController:75, Detail page:63).
   - `Reviews.ProductId` — used by 3 endpoints (~17% of traffic)
   - `Products.Category` — used by 3 endpoints (~17% of traffic)
   - `OrderItems.OrderId` — used by 1 endpoint (~6% of traffic)
   - `Orders.CustomerName` — used by 1 endpoint (~6% of traffic)

   Example addition inside the `CartItem` entity block:
   ```csharp
   entity.HasIndex(e => e.SessionId);
   entity.HasIndex(e => new { e.SessionId, e.ProductId });
   ```

2. After adding the index definitions, generate and apply an EF Core migration.

## Expected Impact

- **p95 latency**: Estimated ~30ms reduction across affected requests. Under peak concurrency (500 VUs) the contention reduction amplifies this — tail requests seeing table-scan lock waits could improve by 50–100ms.
- **RPS**: 3–5% improvement from reduced DB connection hold times and lower lock contention.
- **GC/allocation**: Minor indirect improvement — fewer scanned rows means fewer intermediate buffers allocated during TDS parsing.
- Indexes are the single highest-leverage remaining optimization because they address the root cause (SQL table scans) identified by the CPU profiler across the majority of traffic.
