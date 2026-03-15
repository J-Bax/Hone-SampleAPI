# Root Cause Analysis — Experiment 5

> Generated: 2026-03-15 13:31:00 | Classification: narrow — The optimization replaces client-side `.ToListAsync()` + LINQ `.Where()` with server-side `.Where()` before materialization, which is a single-file query logic change inside method bodies with no dependency, schema, or API contract modifications.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 793.00148ms | 7546.103045ms |
| Requests/sec | 773.4 | 125.5 |
| Error Rate | 0% | 0% |

---
# Replace full CartItems table scans with server-side filtering

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

Three methods in `CartController.cs` load the **entire CartItems table** then filter in memory:

**GetCart** (line 25-26):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```
Plus N+1 product lookups at line 33: `await _context.Products.FindAsync(item.ProductId)`.

**AddToCart** (line 69-71):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

**ClearCart** (line 140-146):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync(); // Saves each time — extra round trips
}
```

The k6 scenario hits these 3 endpoints per iteration: POST `/api/cart` (add), GET `/api/cart/{sessionId}`, DELETE `/api/cart/session/{sessionId}`. Under stress load with 500 VUs, the CartItems table accumulates thousands of rows, and every cart operation transfers the entire table.

## Theory

Each of the three cart API endpoints materializes all CartItems rows just to find a few belonging to one session. Under load, with 500 concurrent VUs each creating cart items, the table grows rapidly. Three full-table scans per iteration × 500 VUs creates enormous unnecessary I/O and memory pressure. The `ClearCart` method compounds this with per-item `SaveChangesAsync` calls, adding N database round-trips. These contribute to the GC pressure (large List<T> allocations) and inflate latency for all concurrent requests.

## Proposed Fixes

1. **Server-side filtering:** Replace `ToListAsync()` + in-memory `.Where()` with EF Core `.Where()` before materialization in all three methods. For GetCart, batch-load products with `.Where(p => productIds.Contains(p.Id))` instead of N+1. For ClearCart, use `RemoveRange` + single `SaveChangesAsync`.
   - GetCart: `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()`
   - AddToCart: `_context.CartItems.FirstOrDefaultAsync(c => c.SessionId == request.SessionId && c.ProductId == request.ProductId)`
   - ClearCart: `var items = await _context.CartItems.Where(...).ToListAsync(); _context.CartItems.RemoveRange(items); await _context.SaveChangesAsync();`

## Expected Impact

- p95 latency: Estimated **200-300ms overall reduction**. Three endpoints × 5.6% each = 16.7% of traffic, each doing unnecessary full table scans.
- GC pressure: Reduced LOH allocations from large List<CartItem> materializations.
- RPS: Moderate improvement from reduced per-request DB I/O.

