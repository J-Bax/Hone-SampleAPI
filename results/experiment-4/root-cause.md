# Root Cause Analysis — Experiment 4

> Generated: 2026-03-15 10:21:33 | Classification: narrow — The fix involves replacing client-side filtering (ToListAsync + in-memory Where) with server-side queries (Where before ToListAsync, Include/Join for related data) entirely within this single PageModel file, with no dependency, schema, or API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 7546.103045ms | 7546.103045ms |
| Requests/sec | 125.5 | 125.5 |
| Error Rate | 0% | 0% |

---
# Orders page loads entire database into memory

> **File:** `SampleApi/Pages/Orders/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Orders/Index.cshtml.cs:40`, the page loads **every order** into memory:

```csharp
var allOrders = await _context.Orders.ToListAsync();
```

At line 46, it then loads **every order item** into memory:

```csharp
var allItems = await _context.OrderItems.ToListAsync();
```

At line 55, inside a `foreach` over matching items, it performs an N+1 product lookup:

```csharp
var product = await _context.Products.FindAsync(item.ProductId);
```

No `.AsNoTracking()` is used anywhere, so EF Core tracks every materialized entity through its change tracker (StateManager, identity maps, NavigationFixer).

The CPU profiler directly confirms this: the only application-code hotspot is `SampleApi.Pages.Orders.IndexModel.<OnGetAsync>b__2(OrderItem)` — the lambda iterating OrderItems. Beneath it, SqlClient row-reading accounts for ~35K samples, EF Core change tracking ~25K samples, SortedDictionary identity-map enumeration ~25K samples, and Unicode string decoding ~17K samples. The memory profiler shows 218GB total allocations with 189 Gen2 collections and a 16.5% GC pause ratio — consistent with loading and tracking massive entity graphs.

Critically, the Orders table **grows continuously** during the k6 run (every VU iteration creates an order via `POST /api/orders`), so each subsequent Orders page request loads more data than the last.

## Theory

The Orders page is the dominant latency driver because it performs full table scans on both the Orders and OrderItems tables, then materializes every row into tracked EF Core entities. With 500 concurrent VUs each creating an order per iteration, the Orders table grows to thousands of rows during the 2-minute test. Each `/Orders?customer=...` request:

1. Loads ALL orders from SQL (full table scan) → thousands of rows materialized
2. Filters in C# memory by customer name → discards most rows
3. Loads ALL order items from SQL (another full table scan)
4. For each matching item, makes a separate `FindAsync` call to Products (N+1)
5. EF Core tracks every entity through its identity map (SortedDictionary), paying per-entity costs for StartTrackingFromQuery, NavigationFixer.InitialFixup, and identity map lookups

This creates ~2.7GB peak heap, triggers 189 Gen2 GC collections with 16.5% pause ratio, and is the root cause of the 7546ms p95 latency.

## Proposed Fixes

1. **Server-side filtering with AsNoTracking and Include:** Replace the full table loads with IQueryable server-side filtering. Use `.AsNoTracking()` since this is a read-only page. Replace the N+1 product lookups with a single joined query:
   - Line 40: Replace `_context.Orders.ToListAsync()` with `_context.Orders.AsNoTracking().Where(o => o.CustomerName == customer).OrderByDescending(o => o.OrderDate).ToListAsync()`
   - Lines 46-64: Replace the full OrderItems load + N+1 loop with a single query joining OrderItems to Products, filtered by the matching order IDs

2. **Add pagination:** Limit the number of orders returned per page (e.g., 20) using `.Skip()` and `.Take()` on the server-side query to cap the maximum work per request.

## Expected Impact

- p95 latency: Expect 50-70% reduction. This page is the #1 CPU and memory hotspot per the profiler. Eliminating full table scans removes the dominant source of the 218GB allocation volume, cutting Gen2 collections from 189 to near-zero.
- RPS: Expect 2-3x improvement as GC pause ratio drops from 16.5% to <2% and SQL server-side load decreases dramatically.
- The improvement compounds over the test duration since the current approach gets progressively slower as the Orders table grows.

