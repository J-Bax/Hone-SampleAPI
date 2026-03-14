# Root Cause Analysis — Experiment 7

> Generated: 2026-03-14 14:31:02 | Classification: narrow — Fixing the N+1 query on line 63 (loading all CartItems then filtering in-memory) to use a WHERE clause is a pure query optimization within a single file, requires no dependency changes, no schema changes, and no API contract changes.

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 482.42257ms | 2054.749925ms |
| Requests/sec | 1321.2 | 427.3 |
| Error Rate | 11.11% | 11.11% |

---
# Product detail OnPost loads entire CartItems table to find one row

> **File:** `SampleApi/Pages/Products/Detail.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Detail.cshtml.cs:63-65`, the `OnPostAsync` method (add-to-cart from product detail page) loads the **entire CartItems table** into memory to check for a duplicate:

```csharp
var allCartItems = await _context.CartItems.ToListAsync();
var existing = allCartItems.FirstOrDefault(c =>
    c.SessionId == sessionId && c.ProductId == productId);
```

The k6 scenario calls this endpoint once per iteration (`POST /Products/Detail/{id}` at line `const addToCartPageRes = http.post(...)`). During the stress phase with 500 VUs, the CartItems table grows rapidly — each VU adds items via both the API (`POST /api/cart`) and this page form, while deletions (cart clear, checkout) lag behind the additions. By mid-test, the table may contain thousands of rows, all loaded into memory just to find 0 or 1 matching rows.

Note: The `OnGetAsync` method in this file was already optimized in experiment 3 (Reviews/Products loading). This is a **separate issue** in `OnPostAsync` that was not addressed.

## Theory

Loading a growing table into memory on every POST creates two problems: (1) linear O(n) cost as n grows throughout the test — early iterations are fast, later iterations under stress are slow, which inflates the p95 tail; (2) each materialized CartItem entity generates allocations (entity object, change tracker entry, string for SessionId), contributing to the 524 MB/sec allocation rate and 32.7% GC pause ratio. The growing nature of this scan means its cost peaks exactly when the system is under maximum stress (500 VUs), amplifying its impact on p95.

## Proposed Fixes

1. **Server-side query:** Replace the full table load with a targeted server-side query:
   ```csharp
   var existing = await _context.CartItems.FirstOrDefaultAsync(c =>
       c.SessionId == sessionId && c.ProductId == productId);
   ```
   This translates to `SELECT TOP 1 ... WHERE SessionId = @p0 AND ProductId = @p1`, returning at most one row regardless of table size.

## Expected Impact

- p95 latency: ~15-30ms reduction per request (eliminates growing table scan and materialization)
- GC pressure: eliminates thousands of unnecessary CartItem allocations per request during stress phase
- The improvement grows as the test progresses — the fix is most impactful exactly when the system is under highest load, which is when p95 is determined

