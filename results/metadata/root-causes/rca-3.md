# Eliminate N+1 queries and per-item SaveChanges in checkout flow

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

The checkout `OnPostAsync` handler (lines 57-118) combines three severe anti-patterns:

1. **Full table scan** — Line 61: `var allCartItems = await _context.CartItems.ToListAsync();` loads every cart item across all sessions, then filters in memory at line 62.

2. **N+1 product lookups in order creation** — Lines 84-100: Each cart item triggers a separate `FindAsync` and `SaveChangesAsync`:
   ```csharp
   foreach (var cartItem in sessionItems)
   {
       var product = await _context.Products.FindAsync(cartItem.ProductId);
       // ...
       _context.OrderItems.Add(new OrderItem { ... });
       // ...
       await _context.SaveChangesAsync(); // line 99 — DB round-trip per item!
   }
   ```

3. **Per-item cart deletion** — Lines 106-110:
   ```csharp
   foreach (var cartItem in sessionItems)
   {
       _context.CartItems.Remove(cartItem);
       await _context.SaveChangesAsync(); // line 109 — another DB round-trip per item!
   }
   ```

The `LoadCartSummary` method (lines 120-147) has the same full-table-scan + N+1 pattern: loads all cart items (line 124), then loops with `FindAsync` per item (line 132).

For a cart with N items, `OnPostAsync` makes: 1 (load all carts) + N (find products) + N (save order items) + 1 (save total) + N (remove cart items) = **3N + 2 database round-trips**. Under the k6 load test, with hundreds of concurrent VUs, this creates massive database contention and serialized I/O waits.

## Theory

The per-item `SaveChangesAsync` pattern is catastrophic under concurrency: each call opens a transaction, writes to disk, and commits — creating serial I/O that cannot be parallelized. With 500 concurrent VUs at peak load, each checkout generates 3N+2 round-trips, saturating the SQL Server connection pool and creating head-of-line blocking.

The CartItems table grows throughout the test as VUs add items faster than checkouts clear them. By mid-test, `_context.CartItems.ToListAsync()` may be materializing thousands of rows, amplifying both the allocation pressure and the GC crisis.

The `LoadCartSummary` N+1 pattern means even the GET (checkout page view) is expensive, and it's called as a fallback in the POST path too (line 67).

## Proposed Fixes

1. **Batch SaveChangesAsync:** Move the `SaveChangesAsync` call outside both loops. Add all OrderItems to the context inside the loop, then call `SaveChangesAsync` once after the loop. Same for cart removal — call `RemoveRange()` and save once.
   - Lines 84-100: Remove `SaveChangesAsync` from line 99, add single save after line 100.
   - Lines 106-110: Replace loop with `_context.CartItems.RemoveRange(sessionItems); await _context.SaveChangesAsync();`

2. **Server-side filtering + eager loading:** Replace `_context.CartItems.ToListAsync()` (lines 61, 124) with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()`. Pre-load products for all cart items in a single query using `_context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)` to eliminate N+1.

## Expected Impact

- **p95 latency:** Estimated 5-7% overall improvement. Batching reduces 3N+2 DB round-trips to 3-4 total. Server-side cart filtering eliminates materializing the entire (growing) CartItems table.
- **DB connection pool:** Dramatically reduced contention from fewer concurrent transactions.
- **Allocation volume:** Moderate reduction from not loading all cart items, contributing to lower GC pressure.
