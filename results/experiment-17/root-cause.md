# Root Cause Analysis — Experiment 17

> Generated: 2026-03-14 20:19:34 | Classification: narrow — Optimization consolidates three SaveChangesAsync calls into two by combining order item additions and total update into a single batch before clearing the cart, which is a pure implementation optimization contained entirely within the OnPostAsync method with no API contract, dependency, or test changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 405.28805ms | 2054.749925ms |
| Requests/sec | 1712.8 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Checkout OnPostAsync uses 3 SaveChangesAsync calls where 2 suffice

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Checkout/Index.cshtml.cs:57-119`, `OnPostAsync` calls `SaveChangesAsync` three times:

```csharp
// Save 1 (line 81): persist order to get auto-generated ID
_context.Orders.Add(order);
await _context.SaveChangesAsync();
```

```csharp
// Save 2 (line 107): persist order items + total amount update
order.TotalAmount = Math.Round(total, 2);
await _context.SaveChangesAsync();
```

```csharp
// Save 3 (lines 110-111): clear cart items
_context.CartItems.RemoveRange(sessionItems);
await _context.SaveChangesAsync();
```

Save 1 is structurally necessary to obtain `order.Id` for the OrderItem foreign keys. However, Saves 2 and 3 are independent write batches — the `RemoveRange` at line 110 stages deletions in the change tracker, and the subsequent `SaveChangesAsync` at line 111 flushes only those deletions. These two saves can be combined.

The checkout POST is called once per VU iteration (5.6% of traffic) and is the most write-intensive operation in the k6 scenario: it creates an Order, adds 1+ OrderItems, updates the order total, and deletes all session CartItems.

## Theory

Each `SaveChangesAsync` opens a database transaction, generates and sends SQL commands (INSERT/UPDATE/DELETE), waits for acknowledgment, and commits. Under 500 concurrent VUs, the checkout endpoint is a write-contention hotspot. The third save forces an additional transaction round-trip for cart deletion that could be included in the second save's transaction batch.

EF Core's change tracker accumulates all pending changes and flushes them together on `SaveChangesAsync`. By staging the cart `RemoveRange` before the second save, all order items, the total update, AND the cart deletions are sent in a single transaction — reducing the number of round-trips from 3 to 2 and the number of transactions from 3 to 2.

This also improves atomicity: if the current code crashes between Save 2 and Save 3, the order is placed but the cart isn't cleared. Combining them ensures both happen atomically.

## Proposed Fixes

1. **Combine Saves 2 and 3:** Move the `RemoveRange(sessionItems)` call (currently at line 110) to before the second `SaveChangesAsync` (line 107), then remove the third `SaveChangesAsync` at line 111. The resulting code stages order items, total update, and cart deletion, then flushes everything in one transaction:
   ```
   // After the foreach loop adding order items:
   order.TotalAmount = Math.Round(total, 2);
   _context.CartItems.RemoveRange(sessionItems);  // stage cart deletion
   await _context.SaveChangesAsync();              // flush all: items + total + cart clear
   ```

## Expected Impact

- p95 latency: ~5-8ms reduction per checkout request from eliminating 1 DB round-trip and transaction commit
- Write contention: reduced lock hold time under concurrent checkouts, benefiting other write operations (cart add, order create API)
- Atomicity: improved correctness — order placement and cart clearing become a single atomic operation
- Overall p95 improvement: estimated 0.5-1% from direct savings plus reduced write-path contention

