# Root Cause Analysis — Experiment 4

> Generated: 2026-03-15 13:05:29 | Classification: narrow — The optimization involves replacing client-side filtering (full-table ToListAsync + in-memory Where) with server-side query filtering, and eliminating the N+1 product lookups via Include/join — all changes are confined to query logic within the single OnGetAsync method of this PageModel, with no contract, dependency, or schema changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 2179.352105ms | 7546.103045ms |
| Requests/sec | 349.3 | 125.5 |
| Error Rate | 0% | 0% |

---
# Eliminate full-table scans and N+1 queries in Orders page

> **File:** `SampleApi/Pages/Orders/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Orders/Index.cshtml.cs:40`, the handler loads the **entire Orders table** into memory:

```csharp
var allOrders = await _context.Orders.ToListAsync();
```

Then at line 46, it loads the **entire OrderItems table**:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
```

Finally, at line 55, it issues an **N+1 query** — one `FindAsync` per order item to resolve product names:

```csharp
var product = await _context.Products.FindAsync(item.ProductId);
```

The CPU profiler confirms this is the dominant bottleneck: `IndexModel.<OnGetAsync>b__2(OrderItem)` accounts for **28.5% inclusive CPU**, and `<OnGetAsync>b__0(Order)` accounts for **22% inclusive CPU**. EF Core's `SingleQueryingEnumerable.MoveNextAsync` shows **18.5% inclusive CPU** materializing massive result sets. Change tracking (`StartTrackingFromQuery`) adds **1.8%** of overhead for what is a read-only page.

The GC report shows an **inverted generation distribution** (Gen2: 171, Gen0: 5) with **15.6% GC pause ratio** and **422.8ms max pause**. Loading all Orders and OrderItems creates large `List<T>` objects that land on the LOH (>85KB), directly triggering Gen2 collections.

## Theory

Every request to `/Orders?customer=X` transfers the entire Orders table, the entire OrderItems table, and then issues N individual product lookups — even though the page only needs orders for a single customer. Under the k6 stress test, orders accumulate rapidly (every VU creates one per iteration), so these tables grow large. The massive in-memory materialization creates LOH allocations, triggering expensive Gen2 GC pauses that directly inflate p95 latency. The client-side filtering (line 42) discards >99% of the data that was already transferred and materialized.

## Proposed Fixes

1. **Server-side filter + eager load + AsNoTracking:** Replace the three separate queries with a single EF Core query that filters by `CustomerName` in SQL, uses `.Include(o => o.OrderItems)` (requires navigation property) or a join, and projects product names via a subquery or batch lookup. Add `.AsNoTracking()` since this is a read-only page. Example approach:
   - Filter orders server-side: `_context.Orders.AsNoTracking().Where(o => o.CustomerName == customer)`
   - Batch-load order items: `_context.OrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId))`
   - Batch-load products: `_context.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`
   - Build the `OrderItemsMap` from the dictionary instead of N+1 queries.

## Expected Impact

- p95 latency: Estimated **400-600ms reduction** overall. The Orders page consumes ~50% of server CPU; eliminating full table scans and N+1 queries will reduce per-request cost by 10-50x.
- GC pressure: Dramatically reduced LOH allocations → fewer Gen2 collections → lower GC pause ratio.
- RPS: Should increase as server spends far less CPU per request.
- This is the single highest-impact fix available based on profiling data.

