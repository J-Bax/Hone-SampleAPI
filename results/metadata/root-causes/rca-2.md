# Checkout page per-item SaveChanges and full table scans

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

In `Pages/Checkout/Index.cshtml.cs`, the `OnPostAsync` method has three critical performance issues:

**Full table scan on CartItems (line 61):**
```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();
```

**N+1 product lookup inside loop (line 86):**
```csharp
var product = await _context.Products.FindAsync(cartItem.ProductId);
```

**Per-item SaveChangesAsync — 2N+1 database round trips (lines 99, 109):**
```csharp
// Inside order item creation loop:
await _context.SaveChangesAsync(); // line 99 — one per cart item

// Inside cart clearing loop:
_context.CartItems.Remove(cartItem);
await _context.SaveChangesAsync(); // line 109 — one per cart item
```

The `LoadCartSummary` method (called by both GET and POST) repeats the same pattern: full CartItems table load (line 124), then N+1 product lookups (line 132).

The CartItems table grows during the test because every VU iteration adds items via `POST /Products/Detail/{id}` and the API `POST /api/cart`.

## Theory

The checkout page is the heaviest transactional endpoint. For a cart with N items, `OnPostAsync` performs: 1 full CartItems table scan + N product lookups + N SaveChanges for order items + 1 SaveChanges for total + N SaveChanges for cart clearing = **3N + 2 database round trips** plus the initial full table load. Each `SaveChangesAsync` incurs a separate SQL transaction with network latency.

As the CartItems table grows (items from hundreds of concurrent sessions), the full table scan at line 61/124 loads increasingly large result sets just to find the few items belonging to one session. Combined with the per-item SaveChanges, this creates severe contention under the 500-VU stress phase.

## Proposed Fixes

1. **Server-side filtering:** Replace `_context.CartItems.ToListAsync()` with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()` at lines 61-62 and 124-125.

2. **Batch SaveChanges:** Move the `SaveChangesAsync()` calls out of the loops. Add all OrderItems and remove all CartItems, then call `SaveChangesAsync()` once at the end. This reduces 3N+2 round trips to 3 (save order, save items+total, remove cart items).

3. **Preload products in bulk:** Before the loop at line 84, collect all product IDs and load them in a single query with `_context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`, then look up from the dictionary inside the loop.

## Expected Impact

- p95 latency: Expect 20-30% reduction for checkout requests. Batching SaveChanges from 3N+2 to 3 round trips and eliminating the full CartItems table scan will cut per-request time significantly.
- Database contention: Fewer transactions under high concurrency reduces lock contention and SQL Server CPU (sqlmin accounted for ~69K samples in the profile).
- Memory: Eliminating unnecessary CartItem entity materialization reduces allocation volume.
