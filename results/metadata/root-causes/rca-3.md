# Add database indexes for sort-heavy queries on Reviews and Orders

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** narrow

## Evidence

At `Pages/Index.cshtml.cs:41-45`, the home page queries recent reviews with a sort on `CreatedAt`:

```csharp
RecentReviews = await _context.Reviews.AsNoTracking()
    .OrderByDescending(r => r.CreatedAt)
    .Take(5)
    .Select(r => new Review { ... })
    .ToListAsync();
```

The current `AppDbContext.cs:43-44` only has an index on `Reviews.ProductId`:
```csharp
entity.HasIndex(e => e.ProductId);
```

There is no index on `Reviews.CreatedAt`, forcing SQL Server to perform a full table scan + sort of the entire Reviews table to find just 5 rows.

Similarly, at `Pages/Orders/Index.cshtml.cs:40-44`, the orders page sorts by `OrderDate`:

```csharp
Orders = await _context.Orders
    .AsNoTracking()
    .Where(o => o.CustomerName == customer)
    .OrderByDescending(o => o.OrderDate)
    .ToListAsync();
```

`AppDbContext.cs:51` has an index on `CustomerName` only — the subsequent `ORDER BY OrderDate DESC` requires an in-memory sort after the index seek.

The CPU profiler attributes 8.2% of samples to `SqlDataReader.TryReadColumnInternal` with the observation that "queries return large result sets with many columns." Full-table scans for sort operations amplify this cost.

## Theory

Without an index on `Reviews.CreatedAt`, the `ORDER BY CreatedAt DESC ... TOP 5` query must scan the entire Reviews table (seed data likely contains thousands of rows) and sort in tempdb before returning 5 rows. With a descending index, SQL Server satisfies the query with an index scan of exactly 5 entries — orders of magnitude less I/O.

For Orders, the existing `CustomerName` index efficiently filters rows, but the `ORDER BY OrderDate DESC` requires sorting the filtered result set in memory. During the load test, each VU creates orders continuously, so a single customer accumulates dozens of orders. A composite index on `(CustomerName, OrderDate)` allows SQL Server to return pre-sorted results directly from the index, eliminating the sort operator entirely.

## Proposed Fixes

1. **Add `Reviews.CreatedAt` index:** In `OnModelCreating`, add `entity.HasIndex(e => e.CreatedAt)` to the Review entity configuration (after line 44).

2. **Replace `Orders.CustomerName` index with composite `(CustomerName, OrderDate)`:** Change line 51 from `entity.HasIndex(e => e.CustomerName)` to `entity.HasIndex(e => new { e.CustomerName, e.OrderDate })` to cover both the WHERE filter and ORDER BY in a single index.

## Expected Impact

- p95 latency: ~2-5ms reduction for home page (Reviews scan → index seek), ~2-5ms for orders page (eliminate in-memory sort)
- Reduced CPU spent on sort operations frees threads for other requests under contention
- Overall p95 improvement: ~0.5-1%
