# Add HasMaxLength to Product.Description to eliminate nvarchar(max) PLP overhead

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

In `Data/AppDbContext.cs` lines 23-30, the Product entity configuration has no max length for Description:
```csharp
modelBuilder.Entity<Product>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
    entity.Property(e => e.Category).HasMaxLength(100).IsRequired();
    entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
    entity.HasIndex(e => e.Category);
});
```

The `Product.Description` property (`Models/Product.cs` line 10: `public string? Description { get; set; }`) maps to `nvarchar(max)` by default in SQL Server.

The CPU profile directly identifies this as expensive:
- `TdsParser.TryReadPlpUnicodeCharsChunk` at **2.2% inclusive** (1,771 samples)
- `UnicodeEncoding.GetCharCount` at 2,264 samples
- `UnicodeEncoding.GetChars` at 1,968 samples
- Total PLP Unicode path: ~6,000 samples

The profiling report states: *"PLP (Partial Length Prefixed) Unicode reading... PLP is used for nvarchar(max) columns — the query is returning large text blobs that dominate data transfer CPU cost."*

In practice, seeded descriptions are ~100 characters (`SeedData.cs` lines 43-44), well within a bounded `nvarchar(500)`.

## Theory

SQL Server uses PLP (Partial Length Prefixed) encoding for `nvarchar(max)` columns, which requires character-by-character chunk reading on the client side via `TryReadPlpUnicodeCharsChunk`. For bounded `nvarchar(N)` where N ≤ 4000, SQL Server uses inline row storage with a fixed-length prefix — the SqlClient reads the value in a single operation without PLP chunking. Since every product query (GetProducts returns all 1,000 products, search, by-category, detail pages) reads the Description column, and `GetProducts` alone is called once per VU iteration, the PLP decoding cost multiplies across hundreds of concurrent requests. The 6,000+ CPU samples on the PLP path represent ~6% of total application CPU.

## Proposed Fixes

1. **Add HasMaxLength(500) to Product.Description:** In `AppDbContext.cs` Product entity configuration (around line 29), add `entity.Property(e => e.Description).HasMaxLength(500);`. Then create and apply a migration (`dotnet ef migrations add LimitDescriptionLength` + `dotnet ef database update`). This changes the column from `nvarchar(max)` to `nvarchar(500)`, eliminating PLP encoding entirely. The seeded data (max ~100 chars) fits comfortably.

## Expected Impact

- p95 latency: ~3-5% reduction. Eliminates ~6% of application CPU overhead from PLP decoding, reducing per-request SQL read cost for all product-related endpoints.
- RPS: ~3-5% increase from freed CPU cycles.
- Memory: slight reduction in per-request allocations — PLP chunking allocates intermediate buffers that bounded nvarchar does not.
