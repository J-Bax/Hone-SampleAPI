# Full-table CartItems loads with N+1 queries and per-item SaveChanges

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25`, `GetCart` loads the entire CartItems table into memory:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

Then at lines 31-33, it issues a separate DB round-trip for each cart item (N+1 pattern):

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

`AddToCart` repeats the full-table load at line 69:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

`ClearCart` at lines 140-147 loads all items, filters in memory, then calls `SaveChangesAsync()` inside the loop — one DB round-trip per deleted item:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

The k6 baseline scenario creates a unique cart per VU+iteration (`k6-session-{VU}-{ITER}`), so the CartItems table grows continuously during the test. Under 500 VUs, thousands of rows accumulate, and every endpoint still loads ALL of them.

## Theory

The CartItems table grows unboundedly during the load test because each VU iteration adds items. Every cart endpoint materializes the entire table via `ToListAsync()`, creating increasingly large `List<CartItem>` arrays. As the table surpasses ~500+ rows, each materialization exceeds the 85KB Large Object Heap threshold, landing directly in Gen2. This explains the inverted GC generation distribution (149 Gen2 vs 4 Gen0) from the diagnostic report.

The N+1 product lookups in `GetCart` add O(n) database round-trips per request. The per-item `SaveChangesAsync` in `ClearCart` adds O(n) write round-trips. Under 500 concurrent VUs, these compound into severe DB connection pool contention and GC pressure.

With 3 out of 5 cart endpoints affected and the table growing during the test, this controller is the single largest contributor to the 895ms p95 latency.

## Proposed Fixes

1. **Server-side filtering with Join:** Replace all `ToListAsync()` + client-side `Where()` patterns with server-side LINQ queries: `_context.CartItems.Where(c => c.SessionId == sessionId)`. For `GetCart`, use a join or two-step query to fetch products in a single round-trip instead of N+1 `FindAsync` calls.

2. **Batch delete in ClearCart:** Replace the per-item `Remove` + `SaveChangesAsync` loop with `RemoveRange(sessionItems)` followed by a single `SaveChangesAsync()` call.

3. **Server-side duplicate check in AddToCart:** Replace the full-table load with `_context.CartItems.FirstOrDefaultAsync(c => c.SessionId == request.SessionId && c.ProductId == request.ProductId)`.

## Expected Impact

- p95 latency: ~30-40% reduction. Eliminating full-table loads on a growing table removes the largest source of LOH allocations and GC pauses. Eliminating N+1 queries removes O(n) DB round-trips.
- RPS: ~30-50% increase from reduced DB connection pool contention and CPU time spent in GC.
- GC pause ratio: Should drop significantly as LOH allocations from CartItems are eliminated.
