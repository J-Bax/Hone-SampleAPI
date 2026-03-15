# Root Cause Analysis — Experiment 2

> Generated: 2026-03-15 04:10:55 | Classification: narrow — All issues (full table scan via ToListAsync, N+1 product lookups in GetCart, per-item SaveChangesAsync in ClearCart, full table scan in AddToCart) can be fixed with server-side .Where() filters, .Include() joins, .AsNoTracking(), and a single batched SaveChangesAsync — all within this one controller file, with no API contract, dependency, or schema changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 1557.26497ms | 1596.242785ms |
| Requests/sec | 474.2 | 468.5 |
| Error Rate | 11.11% | 11.11% |

---
# N+1 queries, full table scans, and per-item SaveChanges in Cart

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

All three cart endpoints exercised by k6 load the entire CartItems table, and two have compounding N+1 / per-item save issues:

**GetCart** (`CartController.cs:25-46`):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    var product = await _context.Products.FindAsync(item.ProductId);
    // ...
}
```
Loads ALL cart items across ALL sessions, filters in memory, then issues a separate `FindAsync` query per item (N+1 pattern).

**AddToCart** (`CartController.cs:69-71`):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```
Loads ALL cart items just to check if one already exists.

**ClearCart** (`CartController.cs:140-147`):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync(); // Saves each time — extra round trips
}
```
Loads ALL cart items, then issues a separate `SaveChangesAsync()` per item removal — N database round trips instead of 1.

The CartItems table grows continuously during the k6 test as 500 VUs add items. By mid-test, this table could contain thousands of rows, making each `ToListAsync()` progressively more expensive. The CPU profile's `SingleQueryingEnumerable.MoveNextAsync` at 12.8% inclusive confirms heavy row enumeration, and the per-item `SaveChangesAsync` in ClearCart multiplies the SQL round-trip overhead.

## Theory

The CartItems table is write-heavy during load testing — every VU iteration adds at least 2 cart items (API POST + Razor Page POST). With 500 VUs and zero think-time, the table grows rapidly. Each of the 3 cart API endpoints loads the ENTIRE table on every request:

1. **Growing table amplification**: At 500 VUs with ~10 iterations each during peak load, the CartItems table could reach 5,000+ rows. `ToListAsync()` materializes all of them with change tracking, creating large List<CartItem> allocations.
2. **N+1 in GetCart**: After loading all cart items, each session's items trigger individual `FindAsync` calls — under load this means dozens of sequential DB round trips per request.
3. **Per-item SaveChanges in ClearCart**: Instead of batching all removals into a single `SaveChangesAsync()`, each item deletion issues a separate SQL DELETE + commit. With N items, this creates N times the connection pool contention under 500 VUs.
4. **Connection pool starvation**: The combination of long-held connections (N+1 queries) and frequent short transactions (per-item saves) contributes to the 11.11% error rate, likely from connection timeouts.

## Proposed Fixes

1. **Server-side filtering + AsNoTracking() for reads**: Replace `_context.CartItems.ToListAsync()` with `_context.CartItems.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync()` in GetCart and ClearCart. In AddToCart, use `_context.CartItems.FirstOrDefaultAsync(c => c.SessionId == request.SessionId && c.ProductId == request.ProductId)` instead of loading all items.

2. **Batch SaveChangesAsync + eliminate N+1**: In GetCart, replace the per-item `FindAsync` loop with a single query joining CartItems to Products (or load all needed product IDs in one `Where(p => productIds.Contains(p.Id))` call). In ClearCart, call `RemoveRange()` and a single `SaveChangesAsync()` instead of per-item saves.

## Expected Impact

- **p95 latency**: Estimated ~250ms reduction per request. Eliminating the full table scan on a growing table prevents progressive degradation. Batching ClearCart saves from N round trips to 1 eliminates N-1 unnecessary DB commits.
- **Error rate**: Reducing connection hold time and round trips should significantly reduce connection pool starvation, lowering the 11.11% error rate.
- **Overall p95 improvement**: ~2.6% (16.7% traffic × 250ms / 1596ms). Error rate reduction may provide additional compound improvement.

