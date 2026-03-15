# Replace full product table scan with server-side filtering and pagination

> **File:** `SampleApi/Pages/Products/Index.cshtml.cs` | **Scope:** narrow

## Evidence

At `Pages/Products/Index.cshtml.cs:35`, the Products listing page loads **every product** into memory with change tracking:

```csharp
var allProducts = await _context.Products.ToListAsync();
```

It then performs category filtering (lines 38-42), search filtering (lines 45-51), and pagination (lines 57-60) **entirely in memory**:

```csharp
if (!string.IsNullOrWhiteSpace(category))
{
    allProducts = allProducts.Where(p =>
        p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
}
...
Products = allProducts
    .Skip((CurrentPage - 1) * PageSize)
    .Take(PageSize)
    .ToList();
```

No `AsNoTracking()` is used, so all materialized entities incur change-tracking overhead. The CPU profile shows SortedDictionary enumeration in EF's identity map at ~5300 samples and type-casting overhead at ~11,500 samples — both are amplified by tracking thousands of unnecessary entities.

## Theory

The Products table likely contains thousands of rows (seeded data). Loading all of them to display only 24 (PageSize) per page is extremely wasteful:

1. **Unnecessary data transfer**: All columns of all product rows cross the TDS wire even though only 24 are displayed. This drives the TDS parsing hotspots in the CPU profile.
2. **Change tracking waste**: Each product entity gets tracked despite being read-only, consuming memory and CPU in the identity map and NavigationFixer.
3. **In-memory operations**: LINQ Where/Skip/Take in memory cannot leverage SQL Server indexes, making the operation O(n) instead of O(log n) for filtered/paginated queries.
4. **Compounding GC effect**: The 800 MB/sec allocation rate is partially driven by repeatedly materializing the full product table on every browse request.

## Proposed Fixes

1. **Build an IQueryable pipeline with server-side filtering and pagination**: Replace the full scan at line 35 with an `IQueryable<Product>` that conditionally applies `.Where()` for category and search filters, then uses `.CountAsync()` for total count and `.Skip().Take().AsNoTracking().ToListAsync()` for the page. This pushes filtering and pagination to SQL Server.

2. **Use AsNoTracking for categories**: The categories query at line 63 (`_context.Categories.ToListAsync()`) should also use `AsNoTracking()` since it's read-only.

## Expected Impact

- p95 latency: ~60-80ms reduction per Products page request
- SQL Server CPU: Reduced query workload by returning only 24 rows instead of thousands
- GC pressure: Dramatically reduced allocations — 24 untracked entities vs thousands of tracked ones
- Overall p95 improvement: ~1-1.5%, with minor indirect benefits from reduced SQL/GC load
