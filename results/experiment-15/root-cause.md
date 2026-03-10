# Root Cause Analysis — Experiment 15

> Generated: 2026-03-10 04:52:55 | Classification: narrow — All N+1 queries and full table scans can be fixed using LINQ `.Where()`, `.Include()`, and batch operations in CartController.cs alone — no DbContext schema changes, no endpoint changes, no new packages required.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 407.84418ms | 888.549155000001ms |
| Requests/sec | 1359.9 | 683.2 |
| Error Rate | 0% | 0% |

---
# Fix full table scans and N+1 queries in CartController

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

Every cart operation loads the entire `CartItems` table into memory then filters client-side:

**GetCart** (line 25-26):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
```

**AddToCart** (line 69-71):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var existing = allItems.FirstOrDefault(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

**ClearCart** (lines 140-146):
```csharp
var allItems = await _context.CartItems.ToListAsync();
var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();
foreach (var item in sessionItems)
{
    _context.CartItems.Remove(item);
    await _context.SaveChangesAsync(); // per-item round trip
}
```

Additionally, GetCart has an N+1 query at line 33:
```csharp
var product = await _context.Products.FindAsync(item.ProductId);
```
This executes a separate SQL query for each cart item.

The k6 baseline scenario hits AddToCart, GetCart, and ClearCart every VU iteration (lines 77-90 of baseline.js). At 500 VUs, the CartItems table grows rapidly during the test — each VU adds an item then clears — creating thousands of rows that are loaded on every request.

## Theory

The CartItems table grows linearly during the load test (up to 500 VUs × multiple iterations). Every cart operation does a full `SELECT * FROM CartItems` regardless of session, transferring and materializing all rows. This creates O(N) SQL read and EF materialization cost where N is the total cart items across all sessions. The N+1 product lookups in GetCart add one SQL round-trip per cart item. The per-item `SaveChangesAsync` in ClearCart adds N database round-trips for deletion. Combined, these three endpoints account for 3 of the 13 requests per VU iteration and likely dominate the SQL read overhead visible in the CPU profile (TdsParserStateObject.TryReadChar at 10.2%, SqlDataReader.TryReadColumnInternal at 3.0%).

The existing composite index on `(SessionId, ProductId)` at AppDbContext line 66 is never utilized because the queries load the entire table instead of using WHERE clauses.

## Proposed Fixes

1. **Replace full table scans with server-side WHERE clauses:** In GetCart (line 25), replace `ToListAsync()` + client filter with `_context.CartItems.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync()`. Apply the same pattern in AddToCart (line 69) using `FirstOrDefaultAsync(c => c.SessionId == request.SessionId && c.ProductId == request.ProductId)`. In ClearCart (line 140), use `Where(c => c.SessionId == sessionId)` server-side.

2. **Eliminate N+1 in GetCart:** Replace the per-item `FindAsync` loop (lines 31-33) with a single batch query: collect all ProductIds, then `_context.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)`. This is the same pattern already used in OrdersController lines 58-62.

3. **Batch ClearCart deletions:** Remove the `SaveChangesAsync()` from inside the foreach loop (line 146) and call it once after the loop. Better yet, use `RemoveRange()` for a single DELETE statement.

## Expected Impact

- p95 latency: ~15-25% reduction (estimated drop to ~310-350ms). Cart operations are 3 of 13 requests per iteration and currently do the most expensive full table scans.
- RPS: ~15-20% increase. Eliminating full table scans frees SQL Server and thread pool capacity.
- Allocation rate: significant reduction — no longer materializing thousands of CartItem entities per request.

