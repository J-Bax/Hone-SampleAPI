# Cart endpoints load entire table and N+1 query products

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-26`, `GetCart` loads every cart item in the database then filters in memory:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

Then at lines 31-33, each session item triggers a separate database round-trip:

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

The same full-table-scan pattern appears in `AddToCart` at lines 69-71:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

And in `ClearCart` at lines 140-146, which additionally calls `SaveChangesAsync()` inside the loop:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

## Theory

Cart endpoints are high-frequency in e-commerce load tests (every user session hits GetCart and AddToCart). Loading the entire `CartItems` table on every request means query cost scales with total rows across ALL sessions, not just the current session. Under concurrent load with k6, the table grows continuously as virtual users add items, making every subsequent request slower.

The N+1 pattern in `GetCart` compounds this: after the full table scan, each cart item issues a separate `SELECT` to the Products table. With 5 items per cart, that's 6 queries per `GetCart` call (1 full scan + 5 product lookups).

`ClearCart` calling `SaveChangesAsync()` inside the foreach loop generates one SQL `DELETE` transaction per item instead of batching, adding unnecessary round-trip latency.

## Proposed Fixes

1. **Server-side filtering with Join:** In `GetCart`, replace the full table scan with `_context.CartItems.Where(c => c.SessionId == sessionId)` and use a `Join` or navigation property to eager-load products in a single query, eliminating the N+1 loop. In `AddToCart`, use `.FirstOrDefaultAsync(c => c.SessionId == ... && c.ProductId == ...)` directly. In `ClearCart`, use `RemoveRange()` and a single `SaveChangesAsync()` after the loop.

2. **Add database index on SessionId:** Configure an index on `CartItem.SessionId` in `AppDbContext.OnModelCreating` to accelerate the filtered queries.

## Expected Impact

- p95 latency: ~15-25% reduction. Cart endpoints currently dominate with full table scans that worsen under load. Server-side filtering eliminates data transfer overhead and reduces SQL execution time.
- RPS: ~15-20% increase. Fewer database round-trips per request frees connection pool and thread pool capacity for concurrent requests.
- Memory: Reduced GC pressure from not materializing entire tables into .NET objects on every call (current 2356MB max heap).
