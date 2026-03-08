# Cart endpoints load entire table and N+1 query for products

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-26`, `GetCart` loads every row from `CartItems` into memory, then filters by session:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

Then at lines 31-33, each session item triggers a separate database round-trip to fetch product data (classic N+1):

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

`AddToCart` repeats the full-table-scan pattern at line 69:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

`ClearCart` at lines 140-147 loads the entire table, filters in memory, then calls `SaveChangesAsync()` inside the `foreach` loopΓÇöone database round-trip per deleted item:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

The `stress-cart.js` k6 scenario exercises all five cart operations (3 adds, 1 read, 1 update, 1 delete-item, 1 clear-session) per VU iteration with up to 200 concurrent VUs, making this the highest-traffic controller.

## Theory

Every cart request pays the cost of `SELECT * FROM CartItems` regardless of how many rows belong to the current session. As the cart table grows under load (each VU iteration creates 3 items), this full scan becomes increasingly expensive. On top of that, `GetCart` issues one `SELECT` per cart item to resolve the product name and priceΓÇöso a session with 3 items generates 1 (full scan) + 3 (product lookups) = 4 database round-trips. `ClearCart` amplifies the issue by flushing to disk once per item removal, creating O(n) transactions instead of one.

Under 200 VUs the CartItems table grows rapidly during the test, and every concurrent request scans the growing table. This creates a quadratic slowdown: more VUs ΓåÆ more rows ΓåÆ slower scans ΓåÆ longer hold on the DB connection pool ΓåÆ cascading latency.

## Proposed Fixes

1. **Server-side filtering with `.Where()`:** Replace all `ToListAsync()` + in-memory `.Where()` patterns with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()`. This pushes the filter to SQL and returns only matching rows.

2. **Eliminate N+1 with a join:** In `GetCart`, use `.Join()` or `.Include()` to load cart items with their product data in a single query instead of looping with `FindAsync`.

3. **Batch `ClearCart` into a single `SaveChangesAsync`:** Move `SaveChangesAsync()` outside the `foreach` loop (line 146ΓåÆafter line 148) so all removes are flushed in one transaction.

4. **In `AddToCart`**, replace the full scan with `_context.CartItems.FirstOrDefaultAsync(c => c.SessionId == ... && c.ProductId == ...)`.

## Expected Impact

- p95 latency: **40-55% reduction** ΓÇö Cart endpoints are exercised on every iteration of the highest-concurrency scenario. Eliminating full table scans and N+1 queries removes the dominant bottleneck.
- RPS: **1.5-2├ù improvement** ΓÇö Freed DB connection pool capacity allows more concurrent requests to complete.
- Error rate: Remains at 0%.
