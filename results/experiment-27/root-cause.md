# Root Cause Analysis — Experiment 27

> Generated: 2026-03-16 09:19:33 | Classification: narrow — Classification skipped (SkipClassification = $true)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | 544.686865ms | 7546.103045ms |
| Requests/sec | 1087.3 | 125.5 |
| Error Rate | 0% | 0% |

---
# Add Select projection excluding Description on product list endpoints and AsNoTracking on GetProduct

> **File:** `SampleApi/Controllers/ProductsController.cs` | **Scope:** narrow

## Evidence

At `ProductsController.cs:25`, the `GetProducts` endpoint returns all 1000 seeded products with every column:

```csharp
var products = await _context.Products.AsNoTracking().ToListAsync();
```

At `ProductsController.cs:75-80`, `SearchProducts` with `q=Product` matches **all 1000 products** because every product name is `"Product XXXX - Category"` (see `SeedData.cs:42`):

```csharp
var results = await _context.Products
    .AsNoTracking()
    .Where(p => EF.Functions.Like(p.Name, $"%{q}%") ||
                EF.Functions.Like(p.Description, $"%{q}%"))
    .ToListAsync();
```

At `ProductsController.cs:83`, the fallback path also returns all products:

```csharp
var allProducts = await _context.Products.AsNoTracking().ToListAsync();
```

At `ProductsController.cs:49-52`, `GetProductsByCategory` returns ~100 full Product entities:

```csharp
var filtered = await _context.Products
    .AsNoTracking()
    .Where(p => p.Category == categoryName)
    .ToListAsync();
```

At `ProductsController.cs:35`, `GetProduct` uses `FindAsync` which attaches the entity to the change tracker unnecessarily for a read-only endpoint:

```csharp
var product = await _context.Products.FindAsync(id);
```

The CPU profiler confirms: `TdsParserStateObject.TryReadChar` (1.1% exclusive), `TryReadPlpUnicodeCharsChunk` (0.45%), `UnicodeEncoding.GetCharCount/GetChars` (0.44%), and `StringConverter.Write` (0.56%) are the top managed CPU consumers — all driven by reading and serializing large string columns. The `Description` field (~95 chars per product, `SeedData.cs:43-44`) is the primary nvarchar payload. With 2×1000 product rows per VU iteration (GetProducts + SearchProducts), this generates ~120,000 Description materializations/sec.

The memory profiler shows 420 MB/sec allocation rate with 88% Gen0→Gen1 promotion and a 91.7ms max GC pause that directly inflates p95 latency.

## Theory

These four endpoints collectively handle ~22% of request traffic (4 of 18 requests per VU iteration). Two of them (`GetProducts` and `SearchProducts?q=Product`) each return all 1000 products, materializing the full `Description` column that is never needed by list consumers. This creates three compounding costs:

1. **SQL read cost**: TDS protocol reads Description character-by-character, generating the highest exclusive-sample CPU methods in the profile.
2. **Allocation pressure**: 1000 Product entities × Description string per request drives the extreme 420 MB/sec allocation rate, pushing objects past Gen0 into Gen1 and triggering the 91.7ms max GC pauses.
3. **JSON serialization cost**: `StringConverter.Write` and `Utf8JsonWriter` serialize ~95KB of Description strings per 1000-product response — the second largest CPU consumer.

`GetProduct`'s use of `FindAsync` adds change-tracker overhead for a read-only path.

## Proposed Fixes

1. **Add `.Select()` projection to `GetProducts`, `SearchProducts`, and `GetProductsByCategory`**: Project only `Id`, `Name`, `Price`, `Category`, `CreatedAt`, `UpdatedAt` — excluding `Description`. This eliminates the largest nvarchar column from list queries without changing the response structure (Description will be null). Apply at lines 25, 75-80, 83, and 49-52.

2. **Replace `FindAsync` with `AsNoTracking().FirstOrDefaultAsync()` in `GetProduct`**: At line 35, change to `_context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id)` to avoid change tracker overhead on this read-only endpoint.

## Expected Impact

- p95 latency: estimated reduction of ~10-20ms on affected endpoints (GetProducts/SearchProducts returning 1000 rows see the largest savings ~15-20ms each; GetProductsByCategory ~3-5ms; GetProduct ~1-2ms)
- Allocation rate: ~5-10% reduction from eliminating ~120,000 Description string materializations/sec
- GC pressure: fewer Gen0→Gen1 promotions, potentially reducing max GC pause from 91.7ms
- Overall p95 improvement: ~2-3% (~10-15ms off 544ms)

