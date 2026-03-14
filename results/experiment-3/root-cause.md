# Root Cause Analysis — Experiment 3

> Generated: 2026-03-14 12:37:32 | Classification: narrow — The N+1 optimization only requires rewriting the OnGetAsync method to use `.Include()` for eager loading and eliminate the product loop — no file additions, no package changes, no schema changes, no API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 1641.868ms | 2054.749925ms |
| Requests/sec | 533.7 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Orders page loads entire Orders and OrderItems tables with N+1 product lookups

> **File:** `SampleApi/Pages/Orders/Index.cshtml.cs` | **Scope:** narrow

## Evidence

CPU profiling identifies `SampleApi.Pages.Orders.IndexModel.<OnGetAsync>` at **45.2% inclusive CPU** — the single largest application hotspot. The method contains three compounding anti-patterns:

1. **Full table scan on Orders** at `Index.cshtml.cs:40`:
```csharp
var allOrders = await _context.Orders.ToListAsync();
Orders = allOrders
    .Where(o => o.CustomerName.Equals(customer, StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(o => o.OrderDate)
    .ToList();
```
Loads every order in the database into memory, then filters client-side. Since each k6 VU creates orders every iteration (via both `POST /api/orders` and `POST /Checkout`), the Orders table grows to tens of thousands of rows during the load test.

2. **Full table scan on OrderItems** at `Index.cshtml.cs:46`:
```csharp
var allItems = await _context.OrderItems.ToListAsync();
```
Loads every order item regardless of which orders matched the customer filter. With 2 items per order, this is 20,000+ rows.

3. **N+1 product lookups** at `Index.cshtml.cs:55`:
```csharp
var product = await _context.Products.FindAsync(item.ProductId);
```
Issues a separate DB round-trip for each order item's product.

The profiling data confirms this: SortedDictionary/SortedSet enumeration at 2.8% CPU (EF Core identity map processing thousands of tracked entities), Dictionary.FindValue at 1.1% (identity map lookups), StateManager.StartTrackingFromQuery at 1.8%, and SqlDataReader.TryReadColumnInternal at 5.1% (deserializing massive result sets). Total allocation rate of 1,472 MB/sec and 2.1 GB peak heap are consistent with materializing full tables per request.

## Theory

The Orders table grows continuously during the k6 load test (each of 500 VUs creates ~2 orders per iteration). By the stress phase, there are tens of thousands of orders. Every `GET /Orders?customer=...` request materializes the entire Orders table (~10K+ rows) and entire OrderItems table (~20K+ rows) into tracked EF Core entities, then filters in-memory. This causes:
- **O(N) memory allocation** per request where N = total orders (not just the customer's)
- **O(N) change tracking overhead** — EF Core's StateManager tracks every entity, performing identity map lookups (Dictionary.FindValue), navigation fixup, and diagnostic logging per entity
- **O(M) additional DB round-trips** for N+1 product lookups where M = number of items in matching orders
- **Severe GC pressure** — the 1,472 MB/sec allocation rate and inverted Gen2:Gen0 ratio (221:41) directly result from repeatedly allocating and discarding these massive collections

Despite being only ~5.6% of traffic (1 of 18 requests per k6 iteration), this endpoint consumes 45% of CPU, starving all other endpoints of resources and inflating their latencies via CPU contention and GC pauses (10.4% pause ratio, max 236ms pause).

## Proposed Fixes

1. **Server-side filtering with AsNoTracking:** Replace the full table scans at lines 40-44 with a server-side `Where` clause and `AsNoTracking()`. Filter orders by customer name in SQL, and use `Include` or a join to load only the related order items. Replace the N+1 product lookups with a single batch query (e.g., load all needed product IDs in one `Where(p => productIds.Contains(p.Id))` call).

2. **Single efficient query:** Combine the orders + items + product names into a single LINQ query with joins and projections, selecting only the fields needed for `OrderItemView`. This eliminates all three table scans and the N+1 pattern in one change.

## Expected Impact

- **p95 latency:** Expect 25-35% overall reduction (~400-575ms). The freed CPU (from 45% down to <5%) eliminates contention that inflates latency for ALL endpoints.
- **RPS:** Expect 30-50% increase as CPU headroom allows more concurrent request processing.
- **Error rate:** The 11.11% error rate may partially stem from GC-induced timeouts under load; reducing allocation volume from this endpoint should help.
- **GC:** Dramatic reduction in allocation rate and heap size as full-table materializations are eliminated. Gen2 collection count and pause ratio should improve significantly.

