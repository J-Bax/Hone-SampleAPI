# Root Cause Analysis — Experiment 38

> Generated: 2026-03-16 20:39:50 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 517.27791ms | 7546.103045ms |
| Requests/sec | 1155.3 | 125.5 |
| Error Rate | 0% | 0% |

---
# Use AsNoTracking and raw SQL DELETE for cart cleanup in checkout

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Checkout/Index.cshtml.cs:61-63`, the checkout POST loads cart items **with change tracking**:

```csharp
var sessionItems = await _context.CartItems
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();
```

These tracked entities are used to read `ProductId` and `Quantity` (lines 92-106), then deleted via `RemoveRange` at line 109:

```csharp
_context.CartItems.RemoveRange(sessionItems);
await _context.SaveChangesAsync();
```

Meanwhile, `CartController.ClearCart` (line 148) already uses the more efficient raw SQL pattern:

```csharp
await _context.Database.ExecuteSqlInterpolatedAsync(
    $"DELETE FROM CartItems WHERE SessionId = {sessionId}");
```

## Theory

Loading cart items with change tracking adds overhead: EF Core creates snapshot copies of each entity, registers them in the identity map, and tracks property changes. When `RemoveRange` is called, EF marks each entity as `Deleted` and generates individual DELETE statements in the `SaveChangesAsync` batch. For N cart items, this means N tracked entities and N DELETE commands in the change tracker.

Using `AsNoTracking()` for the read (since we only need ProductId/Quantity) and `ExecuteSqlInterpolatedAsync` for the delete (a single SQL DELETE statement regardless of item count) eliminates:
- Entity snapshot allocation and tracking overhead
- Per-entity DELETE command generation
- Change tracker processing during SaveChangesAsync

The order creation and the cart deletion can use separate `SaveChangesAsync`/`ExecuteSqlInterpolatedAsync` calls. The cart delete should happen **after** the order save succeeds.

## Proposed Fixes

1. **Add `AsNoTracking()` to the cart items query** at line 61: `.AsNoTracking()` after `_context.CartItems`.
2. **Replace `RemoveRange` with raw SQL DELETE** at line 109: Remove the `_context.CartItems.RemoveRange(sessionItems)` line. After the `SaveChangesAsync()` at line 110 (which saves order items and total), add `await _context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM CartItems WHERE SessionId = {sessionId}");`.

## Expected Impact

- p95 latency: estimated ~5-10ms reduction per checkout request (tracking overhead + SQL generation)
- More impactful under high concurrency where GC pressure from tracked entity snapshots compounds
- Overall p95 improvement: ~0.7% (5.6% traffic share × ~7ms / ~544ms current p95)

