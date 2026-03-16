# Replace FindAsync with AnyAsync for product existence check in AddToCart

> **File:** `SampleApi/Controllers/CartController.cs` | **Scope:** narrow

## Evidence

At `CartController.cs:74`, `AddToCart` uses `FindAsync` to check if a product exists:

```csharp
var product = await _context.Products.FindAsync(request.ProductId);
if (product == null)
    return NotFound(new { message = $"Product with ID {request.ProductId} not found" });
```

The loaded `Product` entity (including the ~95 char Description field) is never used after the null check — it exists solely for existence validation. `FindAsync` also attaches the entity to the change tracker (`EntityState.Unchanged`), adding memory overhead.

At `CartController.cs:78-79`, the subsequent `FirstOrDefaultAsync` for cart item deduplication does not use `AsNoTracking`:

```csharp
var existing = await _context.CartItems.FirstOrDefaultAsync(c =>
    c.SessionId == request.SessionId && c.ProductId == request.ProductId);
```

This is correct since the `existing` entity may be modified at line 84 (`existing.Quantity += request.Quantity`). However, the Product loaded at line 74 remains tracked unnecessarily, consuming change tracker memory for the remainder of the request.

The CPU profiler shows `DI.ResolveService` at 0.17% and `SqlDataReader (aggregate)` at 3.8% — both scale with per-request entity count. The memory profiler's 88% Gen0→Gen1 promotion ratio suggests per-request objects (like unnecessary Product entities) surviving async awaits.

## Theory

`POST /api/cart` accounts for ~5.6% of traffic. Each call materializes a full Product entity (with Description, category, timestamps) solely to check `!= null`. This generates:

1. A `SELECT TOP 1 *` query reading all columns from the Products table
2. Entity materialization allocating a Product object + Description string (~95 bytes)
3. Change tracker entry creation (state object, original values snapshot)

`AnyAsync` translates to `SELECT CASE WHEN EXISTS (SELECT 1 FROM Products WHERE Id = @p) THEN 1 ELSE 0 END` — a single bit result with no entity materialization, no column reads, and no change tracker overhead.

## Proposed Fixes

1. **Replace `FindAsync` with `AnyAsync` at line 74**: Change to `var productExists = await _context.Products.AnyAsync(p => p.Id == request.ProductId);` and check `if (!productExists)`. This eliminates the full Product entity read entirely.

## Expected Impact

- Per-request latency: ~3-5ms reduction (eliminates one full entity read + change tracker overhead)
- Allocation: eliminates one Product + Description materialization per AddToCart call
- Overall p95 improvement: ~0.3-0.5% (~2ms off 544ms)
