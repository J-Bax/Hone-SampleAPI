# Root Cause Analysis — Experiment 15

> Generated: 2026-03-15 19:40:18 | Classification: narrow — Adding .AsNoTracking() to the read-only queries (Reviews and RelatedProducts) in OnGetAsync is a single-file change to method internals that does not alter any API contract, dependency, or schema.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 548.763115ms | 7546.103045ms |
| Requests/sec | 1032.4 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add AsNoTracking to all read-only queries on Detail page

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Detail.cshtml.cs:30`, the product is loaded with `FindAsync` which always tracks the entity:

```csharp
Product = await _context.Products.FindAsync(id);  // line 30 — tracked
```

At lines 34-37, reviews are loaded without `AsNoTracking()`:

```csharp
Reviews = await _context.Reviews
    .Where(r => r.ProductId == id)
    .OrderByDescending(r => r.CreatedAt)
    .ToListAsync();  // lines 34-37 — tracked
```

At lines 41-44, related products are also loaded without `AsNoTracking()`:

```csharp
RelatedProducts = await _context.Products
    .Where(p => p.Category == Product.Category && p.Id != id)
    .Take(4)
    .ToListAsync();  // lines 41-44 — tracked
```

The GC profile shows **86% Gen0-to-Gen1 escalation** and **~395 KB/request allocation**. Change-tracking creates identity map entries and property snapshots for every tracked entity — these mid-lived objects survive Gen0 (because the DbContext is scoped to the request) and get promoted to Gen1, directly causing the observed escalation pattern.

The Detail page is hit **twice per k6 iteration** (GET at request 13, POST at request 14 which re-calls `OnGetAsync` at line 85), so ~11% of total traffic exercises these 3 tracked queries.

## Theory

EF Core change tracking creates a snapshot copy of every tracked entity's property values for dirty detection. For the Detail page, this means:
- 1 Product snapshot (with Description — nvarchar(max))
- Up to 7 Review snapshots (each with Comment — nvarchar(2000))
- 4 related Product snapshots (each with Description)

These snapshots are mid-lived (alive for the request duration), causing Gen0→Gen1 promotion. The `OnPostAsync` method only modifies CartItems — the Product, Reviews, and RelatedProducts are never modified, so tracking is pure overhead. The max GC pause of 63.7ms (from the memory profile) directly contributes to the 548ms p95.

## Proposed Fixes

1. **Replace `FindAsync` with `AsNoTracking().FirstOrDefaultAsync()`** at line 30:
   ```csharp
   Product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
   ```

2. **Add `.AsNoTracking()`** to the Reviews query at line 34 and the RelatedProducts query at line 41.

## Expected Impact

- **Memory**: reduces per-request allocation by ~15-25 KB (eliminated snapshot copies for ~12 entities × ~1-2 KB each)
- **GC**: fewer Gen1 promotions → lower max GC pause
- **p95 latency**: ~5-10ms improvement from reduced GC pauses and faster entity materialization (no snapshot creation)

