# Checkout OnPostAsync has excessive SaveChangesAsync calls and N+1 queries

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Checkout/Index.cshtml.cs:61-62`, the checkout POST loads all cart items then filters in memory:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();
```

At lines 84-100, each cart item triggers a separate product lookup AND a separate `SaveChangesAsync`:

```csharp
foreach (var cartItem in sessionItems)
{
    var product = await _context.Products.FindAsync(cartItem.ProductId);
    ...
    await _context.SaveChangesAsync();
}
```

Then at lines 106-110, cart clearing also calls `SaveChangesAsync` per item:

```csharp
foreach (var cartItem in sessionItems)
{
    _context.CartItems.Remove(cartItem);
    await _context.SaveChangesAsync();
}
```

With 2 cart items (as per the test), this results in: 1 (load all cart items) + 1 (save order) + 2 (product finds) + 2 (save per order item) + 1 (save total) + 2 (save per cart remove) = 9 database round trips instead of ~3.

## Theory

Checkout is called once per VU iteration (~5% of traffic) but is by far the heaviest single request due to multiple sequential `SaveChangesAsync` calls. Each `SaveChangesAsync` is a separate database round trip with connection overhead. With 36 concurrent VUs at peak, this creates significant database contention. Batching writes into fewer saves would cut the number of round trips roughly in half.

## Proposed Fixes

1. **Batch SaveChangesAsync:** Move the `SaveChangesAsync` at line 99 out of the foreach loop, calling it once after all order items are added. Similarly, batch the cart clearing removes at lines 106-110 into a single `RemoveRange` + one `SaveChangesAsync`.
2. **Server-side cart filtering:** Replace lines 61-62 with a filtered query: `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()`.

## Expected Impact

- p95 latency: reduction of ~6-8ms per checkout request by reducing round trips from ~9 to ~3.
- Overall p95 improvement: ~2-3% since checkout is ~5% of traffic but has outsized latency.