# Cart endpoints: full-table scans, N+1 queries, and per-item saves

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

**GetCart (line 25-26)** loads the entire `CartItems` table into memory, then filters client-side:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

It then issues a **separate `FindAsync` per cart item** (N+1 pattern, lines 33):

```csharp
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```

**AddToCart (lines 69-71)** also loads ALL cart items to check for a duplicate:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

**ClearCart (lines 140-147)** loads ALL cart items, filters client-side, and calls `SaveChangesAsync()` inside the loop — one DB round-trip per item:

```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync();
}
```

The `CartItems` table **grows during the k6 run** (each of 500 VUs adds items), making every full-table scan progressively slower under load.

## Theory

Three compounding anti-patterns make the cart endpoints the worst bottleneck:

1. **Full-table scans on a growing table:** Every cart operation loads ALL rows from `CartItems` regardless of session. At peak load with 500 VUs, this table can hold thousands of rows, and every request pays the cost of transferring and materializing them all.

2. **N+1 queries in GetCart:** After loading cart items, each item triggers an individual `FindAsync` for its product. With even 1-2 items per session, this is 2-3 DB round-trips; the real damage is that 500 concurrent VUs each doing N+1 creates severe connection pool contention.

3. **Per-item SaveChanges in ClearCart:** Instead of batching the delete into one operation, each item removal triggers a separate DB write. This multiplies write latency by the number of items and holds the DB connection longer.

## Proposed Fixes

1. **Replace full-table scans with server-side WHERE clauses:** In GetCart, AddToCart, and ClearCart, replace `_context.CartItems.ToListAsync()` followed by `.Where()` with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()` to push filtering to SQL.

2. **Eliminate N+1 in GetCart:** Use a LINQ join or `Include`-based query to load cart items with their associated product data in a single query. Since `CartItem` has no navigation property, use a join: `from ci in _context.CartItems join p in _context.Products on ci.ProductId equals p.Id where ci.SessionId == sessionId select new { ci, p }`.

3. **Batch ClearCart deletes:** Replace the per-item `Remove` + `SaveChangesAsync` loop with `RemoveRange(sessionItems)` followed by a single `SaveChangesAsync()` call.

## Expected Impact

- **p95 latency:** GetCart's N+1 elimination should save ~100-200ms per request. Full-table scan removal saves ~50-100ms per request across all 3 endpoints. ClearCart batching saves ~50-150ms.
- **DB connection pressure:** Dramatically reduced — fewer queries per request means fewer concurrent connections under load, improving all endpoints indirectly.
- **Overall p95 improvement:** ~12-18% (estimated ~150ms average reduction across 23% of traffic, plus reduced contention benefits).
