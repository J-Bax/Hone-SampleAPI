# Batch SaveChangesAsync calls in checkout flow

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Checkout/Index.cshtml.cs:84-100`, each cart item triggers a separate `SaveChangesAsync`:

```csharp
foreach (var cartItem in sessionItems)
{
    var product = await _context.Products.FindAsync(cartItem.ProductId);
    // ...
    _context.OrderItems.Add(new OrderItem { ... });
    total += price * cartItem.Quantity;
    await _context.SaveChangesAsync(); // line 99 — save per item!
}
```

Then at lines 106-110, cart items are removed one-by-one with a save per removal:

```csharp
foreach (var cartItem in sessionItems)
{
    _context.CartItems.Remove(cartItem);
    await _context.SaveChangesAsync(); // line 109 — save per removal!
}
```

Additionally at line 61, ALL cart items across all sessions are loaded into memory:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();
```

## Theory

With 2 cart items per iteration (the test adds 2 items, removes 1, leaving 1), the checkout flow issues: 1 SaveChangesAsync for order creation (line 80), N calls for order items (line 99), 1 for total update (line 103), and N more for cart clearing (line 109). That's ~5 round-trips to the DB instead of 2-3. Under concurrency (up to 36 VUs), this serializes many small writes and creates lock contention. Loading all cart items globally (line 61) is also wasteful.

## Proposed Fixes

1. **Batch all writes:** Add all OrderItems to the context in the loop without saving, then call `SaveChangesAsync` once after the loop. For cart clearing, use `RemoveRange` and a single `SaveChangesAsync`.
2. **Filter cart items server-side:** Replace `ToListAsync()` + in-memory `.Where()` with `_context.CartItems.Where(c => c.SessionId == sessionId).ToListAsync()`.

## Expected Impact

- p95 latency: ~2-3ms reduction on checkout requests
- Reduces DB round-trips from ~5 to ~2-3 per checkout
- Overall p95 improvement: ~3-5% given checkout is ~5% of traffic but is one of the heaviest endpoints