# Add Select projection to product lookup in Cart page LoadCart

> **File:** `SampleApi/Pages/Cart/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Cart/Index.cshtml.cs:117-121`, `LoadCart` fetches full `Product` entities when only `Id`, `Name`, and `Price` are used:

```csharp
var products = await _context.Products
    .AsNoTracking()
    .Where(p => productIds.Contains(p.Id))
    .ToDictionaryAsync(p => p.Id);   // Full Product entities
```

Only `Name` and `Price` are accessed (lines 131-132):
```csharp
ProductName = product?.Name ?? "Unknown",
ProductPrice = product?.Price ?? 0m,
```

By contrast, the Checkout page (`Checkout/Index.cshtml.cs:133-137`) already uses the optimized pattern:
```csharp
.Select(p => new { p.Id, p.Name, p.Price })
.ToDictionaryAsync(p => p.Id);
```

The CPU profiler identifies `TdsParserStateObject.TryReadChar` at 1.1% exclusive and `TryReadPlpUnicodeCharsChunk` at 0.22% — both indicate excessive Unicode string data (Description, Category) being read from SQL Server but never used. The memory profiler reports ~422 MB/sec allocation rate; full entity materialization contributes unnecessary `string` allocations for unused properties.

## Theory

Loading full `Product` entities transfers all columns including `Description` (potentially large nvarchar), `Category`, `CreatedAt`, and `UpdatedAt` from SQL Server through the TDS wire protocol. Each unused column adds: (1) SQL Server I/O to read the column, (2) TDS protocol bytes on the wire, (3) .NET string allocations for materialization, (4) GC pressure from short-lived string objects. The GC profiler shows 86.7% Gen0→Gen1 promotion — mid-life objects from entity materialization that survive Gen0 but die in Gen1, causing 160ms max Gen1 pauses. Projecting only needed columns reduces all four costs.

## Proposed Fixes

1. **Add Select projection:** Replace the full entity query with `.Select(p => new { p.Id, p.Name, p.Price }).ToDictionaryAsync(p => p.Id)` — identical to the pattern already used in `Checkout/Index.cshtml.cs:136-137`. Update the `TryGetValue` and property access on lines 125-132 to use the anonymous type.

## Expected Impact

- p95 latency: ~2-3ms reduction per cart page request (less SQL data, fewer allocations)
- GC pressure: reduced mid-life allocations contributing to Gen1 pause spikes
- This endpoint is ~5.5% of traffic. Overall p95 improvement: ~0.3%.
