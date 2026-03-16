# Add AsNoTracking and Select projections to Checkout read queries

> **File:** `SampleApi/Pages/Checkout/Index.cshtml.cs` | **Scope:** narrow

## Evidence

In `LoadCartSummary()` (called by `OnGetAsync`), both queries lack `AsNoTracking()` and load full entities:

```csharp
var sessionItems = await _context.CartItems
    .Where(c => c.SessionId == sessionId)
    .ToListAsync();  // line 122-124 — tracked, full entity
```

```csharp
var products = await _context.Products
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);  // line 130-132 — tracked, full entity including Description
```

The Products query loads all columns including `Description` (nvarchar(max)) but only uses `Id`, `Name`, and `Price` (lines 136-138):

```csharp
ProductName = product?.Name ?? "Unknown",
ProductPrice = product?.Price ?? 0m,
```

Similarly, in `OnPostAsync` at lines 83-86, the Products dictionary is loaded with full entities but only `Id` and `Price` are used:

```csharp
var products = await _context.Products
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);  // line 84-86 — full entity, only Price used
```

The CPU profile identifies **TryReadPlpUnicodeCharsChunk** in the TDS parsing hotspot, indicating large nvarchar columns (like `Description`) being decoded character-by-character. With 1000 products each having ~90-character descriptions, loading unnecessary Description data wastes TDS parsing CPU.

The Checkout page is hit **twice per k6 iteration** (GET + POST) = ~11% of traffic.

## Theory

The Checkout page loads full Product entities (all 6 columns) when only 2-3 columns are needed. The `Description` field (nvarchar(max)) is the most expensive to transfer and decode — SQL Server sends the full column data over TDS, which is parsed character-by-character as shown in the CPU profile. Additionally, without `AsNoTracking()`, EF Core creates change-tracking snapshots for both CartItems and Products, contributing to the 86% Gen0→Gen1 escalation rate.

In `LoadCartSummary`, the tracked CartItems are never modified (read-only display). In `OnPostAsync`, the Products are never modified (only read for prices). Both are pure tracking overhead.

## Proposed Fixes

1. **Add `AsNoTracking()` to both queries in `LoadCartSummary()`** (lines 122 and 130).

2. **Use `.Select()` projection for Products in `LoadCartSummary()`** to load only `Id`, `Name`, `Price`:
   ```csharp
   .Select(p => new { p.Id, p.Name, p.Price })
   ```

3. **Add `AsNoTracking()` and `.Select(p => new { p.Id, p.Price })` for Products in `OnPostAsync()`** at line 84 — only `Price` is needed for order total calculation.

## Expected Impact

- **TDS parsing**: eliminates transfer of `Description`, `Category`, `CreatedAt`, `UpdatedAt` columns for product lookups — reduces TDS bytes parsed per request.
- **Memory**: eliminates tracking snapshots for CartItems (read path) and Products (both paths) — reduces Gen1 promotions.
- **p95 latency**: ~5-8ms improvement from combined TDS and GC savings.
