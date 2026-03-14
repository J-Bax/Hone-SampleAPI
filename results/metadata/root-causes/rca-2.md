# Checkout page has per-item SaveChanges, full table scans, and N+1 queries

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

The checkout page has three distinct performance issues across its handlers:

1. **Full table scan of CartItems** at `Index.cshtml.cs:61` and `Index.cshtml.cs:124`:
```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();
```
Both `OnPostAsync` (line 61) and `LoadCartSummary` (line 124) load the entire CartItems table into memory, then filter by session ID in C#. Under load with 500 VUs, this table accumulates entries from concurrent sessions.

2. **Per-item SaveChangesAsync in OnPostAsync** at lines 86-99:
```csharp
foreach (var cartItem in sessionItems)
{
    var product = await _context.Products.FindAsync(cartItem.ProductId);
    // ...
    _context.OrderItems.Add(new OrderItem { ... });
    await _context.SaveChangesAsync(); // line 99 — DB round-trip per item!
}
```
Then again at lines 106-110 for cart cleanup:
```csharp
foreach (var cartItem in sessionItems)
{
    _context.CartItems.Remove(cartItem);
    await _context.SaveChangesAsync(); // line 109 — another round-trip per item!
}
```
For a 1-item cart, this is 4 SaveChanges calls (order create, item add, order total update, cart remove). For N items it's 2+N+1+N = 2N+3 DB round-trips.

3. **N+1 product lookups in LoadCartSummary** at `Index.cshtml.cs:132`:
```csharp
var product = await _context.Products.FindAsync(item.ProductId);
```

## Theory

The POST /Checkout handler is the heaviest single operation in the k6 scenario — it creates an order, adds items, updates the total, and clears the cart, each with a separate DB transaction. Under high concurrency (500 VUs), the per-item SaveChanges pattern serializes DB writes and holds connections longer than necessary, creating connection pool contention. The full table scan of CartItems also grows with concurrent sessions. The GET handler's LoadCartSummary duplicates the same full-scan + N+1 pattern.

Checkout traffic is ~11.1% of requests (GET + POST, 2 of 18 per iteration). The POST is particularly expensive due to multiple DB round-trips.

## Proposed Fixes

1. **Batch all writes into a single SaveChangesAsync:** In `OnPostAsync`, add all OrderItems to the context in the loop without calling SaveChanges, then call SaveChangesAsync once after the loop. Same for cart cleanup — use `RemoveRange` and a single save. This reduces 2N+3 round-trips to 3 (order create, items+total, cart clear).

2. **Server-side CartItems filtering:** Replace `ToListAsync()` + LINQ `Where` with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()` at lines 61 and 124. Replace N+1 product lookups with a batch `Where(p => ids.Contains(p.Id))` query.

## Expected Impact

- **p95 latency:** ~8-12% overall p95 reduction (~130-200ms). The batched saves eliminate 2N extra DB round-trips per checkout, and server-side filtering reduces memory allocations.
- **RPS:** Moderate improvement from reduced DB connection hold times.
- **Error rate:** May improve if errors stem from connection pool exhaustion or timeouts caused by long-held connections.
