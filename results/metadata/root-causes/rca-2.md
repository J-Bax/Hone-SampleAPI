# Consolidate two SaveChangesAsync into one in checkout OnPostAsync

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Checkout/Index.cshtml.cs:82`, the order is saved to obtain its `Id`:

```csharp
_context.Orders.Add(order);
await _context.SaveChangesAsync(); // Save to get ID
```

Then at line 110, after all OrderItems are added (lines 98-104) and the total is computed, a second save is issued:

```csharp
await _context.SaveChangesAsync(); // Save order items and update total
```

This is the same two-save pattern as `OrdersController.CreateOrder`. The checkout POST (`POST /Checkout`) runs every k6 iteration, adding ~65 unnecessary DB round trips/sec under peak load.

The checkout path already has 5 DB round trips (cart query, order save, product query, items+total save, cart delete). Eliminating one brings it to 4, a 20% reduction in round trips for this endpoint.

## Theory

Identical to the OrdersController case: EF Core's temporary key fixup allows `order.Id` to hold a temp value after `_context.Orders.Add(order)`. Setting `OrderId = order.Id` on each `OrderItem` before calling a single `SaveChangesAsync()` lets EF resolve the real FK during insert. The second save at line 110 already handles OrderItems, the Order total update, and then the raw SQL cart delete follows at line 111. Only the first save (line 82) is redundant.

## Proposed Fixes

1. **Remove the first SaveChangesAsync at line 82.** The `_context.Orders.Add(order)` call remains, giving `order.Id` a temp key. OrderItems at line 98-104 already reference `order.Id`. Move the single `SaveChangesAsync()` to where line 110 currently is, saving the Order + all OrderItems + the updated total in one DB round trip. The raw SQL DELETE at line 111-112 follows as a separate statement (it must remain separate since it uses `ExecuteSqlInterpolatedAsync`).

## Expected Impact

- p95 latency: ~10-15ms reduction per checkout request
- Checkout round trips: 5 → 4 (20% fewer per request)
- Indirect: same DB contention reduction as opportunity #1, compounding the benefit
- Overall p95 improvement: ~1-2%
