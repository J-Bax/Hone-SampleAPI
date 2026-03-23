# Root Cause Analysis — Experiment 4

> Generated: 2026-03-23 03:22:05 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 27950.345ms | 27950.345ms |
| Requests/sec | 20.1 | 20.1 |
| Error Rate | 100% | 100% |

---
# AddToCart performs 3 sequential DB round trips under high concurrency

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:67-96`, the `AddToCart` method executes three sequential database operations:

```csharp
// Round trip 1: Check product exists (line 70)
var product = await _context.Products.FindAsync(request.ProductId);

// Round trip 2: Check for existing cart item (lines 74-75)
var existing = await _context.CartItems.FirstOrDefaultAsync(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);

// Round trip 3: Save changes (line 81 or 94)
await _context.SaveChangesAsync();
```

Each round trip acquires and holds a SQL connection from the pool. Under 500 concurrent VUs (the k6 stress stage), this endpoint is called once per VU iteration (~5.6% of total traffic). With 500 concurrent requests each holding connections for 3 sequential DB operations, the default SQL Server connection pool (100 connections) is overwhelmed. The connection string in `appsettings.json` confirms no custom pool size:

```json
"DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=HoneSampleDb;Trusted_Connection=True;MultipleActiveResultSets=true"
```

No `Max Pool Size` is configured, so the default of 100 applies.

## Theory

The `FindAsync` on line 70 is purely a product-existence check — the result is only used for a null check and never read otherwise. This wastes one full DB round trip per request. The `CartItem` table already has a foreign key on `ProductId` (implied by EF conventions), so inserting a cart item with an invalid `ProductId` would throw a `DbUpdateException` that can be caught.

Under 500 VUs, each AddToCart call holds a connection for ~3× the time of a single-operation endpoint. With 500 concurrent calls × 3 operations = up to 1500 queued connection requests against a pool of 100, causing massive connection wait times (approaching the 30s default command timeout) and ultimately 100% request failures.

## Proposed Fixes

1. **Remove the redundant product-existence check:** Delete lines 70-72 (the `FindAsync` + null check). Instead, wrap the `SaveChangesAsync` call in a try/catch for `DbUpdateException` to return a 404-equivalent if the FK constraint is violated. This reduces round trips from 3 to 2, cutting connection hold time by ~33%.

2. **Combine the upsert into a single query:** Replace the `FirstOrDefaultAsync` + conditional save with a raw SQL `MERGE`/upsert statement via `ExecuteSqlInterpolatedAsync`, reducing to a single DB round trip.

## Expected Impact

- Connection hold time per AddToCart request reduced by 33-66% (3 ops → 2 or 1)
- Under 500 VUs, concurrent connection demand from this endpoint drops from ~1500 to ~1000 or ~500 queued operations
- p95 latency improvement: ~5-10% overall (this endpoint is 5.6% of traffic but its connection pressure cascades to all other endpoints waiting for pool connections)
- Error rate should decrease as connection pool contention is reduced

