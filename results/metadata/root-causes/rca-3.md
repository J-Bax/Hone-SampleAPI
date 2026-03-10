# Fix N+1 queries, full table scans, and per-item SaveChanges in CartController

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

`GetCart` loads ALL cart items then filters client-side, plus executes an N+1 query loop:

`CartController.cs:25-26`:
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

`CartController.cs:33`:
```csharp
var product = await _context.Products.FindAsync(item.ProductId);
```
This runs inside a `foreach` loop (line 31), executing one SQL query per cart item.

`AddToCart` also loads all cart items to find an existing entry:

`CartController.cs:69-71`:
```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

`ClearCart` loads all items and calls `SaveChangesAsync` per item:

`CartController.cs:140-146`:
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync(); // Saves each time — extra round trips
}
```

## Theory

The CartItems table grows during the load test — each VU iteration adds an item via `POST /api/cart`. With 500 VUs ramping over 120s, thousands of cart items accumulate. Every `GetCart`, `AddToCart`, and `ClearCart` call loads this growing table into memory. As the table grows, materialization time and allocation volume increase linearly throughout the test, creating progressively worse GC pressure.

The N+1 pattern in `GetCart` (line 33) adds sequential round-trips to SQL Server for each cart item's product. While individual `FindAsync` calls hit the EF identity cache if the product was already tracked, under `AsNoTracking` or fresh DbContext scopes, each becomes a SQL query.

The per-item `SaveChangesAsync` in `ClearCart` (line 146) creates unnecessary database round-trips — if a session has 5 items, that's 5 separate SQL DELETE transactions instead of one.

## Proposed Fixes

1. **Server-side filtering**: Replace `ToListAsync()` + LINQ `.Where()` with `_context.CartItems.Where(c => c.SessionId == sessionId).AsNoTracking().ToListAsync()` in `GetCart` (line 25-26), and similar in `AddToCart` (line 69-71) and `ClearCart` (line 140-141).

2. **Eliminate N+1 in GetCart**: Replace the foreach+FindAsync loop (lines 31-47) with a single joined query: load session cart items with their product data using `.Join()` or load the needed product IDs and fetch products in one `Where(p => productIds.Contains(p.Id))` query.

3. **Batch SaveChanges in ClearCart**: Move `SaveChangesAsync()` outside the foreach loop (line 146) so all removals are committed in a single transaction.

## Expected Impact

- **p95 latency**: ~10-15% reduction. Cart endpoints are hit 3 times per VU iteration (add, get, clear). Eliminating full table scans on a growing table and N+1 queries removes both the linear growth problem and sequential round-trips.
- **RPS**: ~10-15% increase from fewer SQL round-trips and less materialization.
- **GC**: Moderate reduction — the CartItems table is smaller than Products/Reviews initially, but grows unboundedly during the test, making this increasingly impactful.
