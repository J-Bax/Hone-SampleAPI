# N+1 queries and full CartItems table scan on every cart operation

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:25-26`, `GetCart` loads ALL cart items across ALL sessions, then filters in memory:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

At `CartController.cs:33`, inside the foreach loop, each cart item triggers a separate database query:

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

At `CartController.cs:69-71`, `AddToCart` loads ALL cart items to check for a duplicate:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

At `CartController.cs:140-146`, `ClearCart` loads all items and then calls `SaveChangesAsync()` inside a loop — one database round-trip per item:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync(); // Saves each time — extra round trips
}
```

The CartItems table grows during the test (each VU adds items), so the full-table scan cost increases over time. The k6 scenario executes add-to-cart, get-cart, and clear-cart every iteration (baseline.js lines 77-90).

## Theory

The CartItems table is write-heavy and grows throughout the load test. Unlike the static Products (1,000) and Reviews (~2,000) tables, CartItems accumulates new rows from every VU iteration. With 500 VUs, the table can grow to thousands of rows mid-test. Each `GetCart` call loads the full growing table plus issues N+1 individual `FindAsync` calls for product lookups. `ClearCart` issues one `SaveChangesAsync` per item, creating N database round-trips instead of one batch delete. `AddToCart` loads all cart items just to check if a specific (sessionId, productId) combination exists — a query that should be a simple `WHERE` clause.

The N+1 pattern in `GetCart` (line 33) amplifies latency linearly with cart size, and the full-table load on every cart operation means allocation pressure grows throughout the test.

## Proposed Fixes

1. **Server-side filtering + join for GetCart:** Replace lines 25-26 with `_context.CartItems.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync()`. Replace the N+1 loop (lines 31-47) by pre-loading needed products in a single query: `var productIds = sessionItems.Select(i => i.ProductId).Distinct(); var products = await _context.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);` then look up from the dictionary.

2. **Fix AddToCart and ClearCart:** In `AddToCart` (line 69-71), replace with `_context.CartItems.FirstOrDefaultAsync(c => c.SessionId == request.SessionId && c.ProductId == request.ProductId)`. In `ClearCart` (line 140-146), use `RemoveRange` + single `SaveChangesAsync`: filter server-side, call `_context.CartItems.RemoveRange(sessionItems)`, then `await _context.SaveChangesAsync()` once.

## Expected Impact

- **p95 latency:** ~10-15% additional reduction. Eliminates N+1 queries and growing full-table scans on the write-heavy table.
- **RPS:** ~10% increase from fewer database round-trips per request.
- **Error rate:** Remains at 0% — no functional changes.
- **Tail latency improvement:** The ClearCart fix (single SaveChanges vs N round-trips) will significantly reduce worst-case latency for cart cleanup operations, especially as cart sizes grow during the test.
