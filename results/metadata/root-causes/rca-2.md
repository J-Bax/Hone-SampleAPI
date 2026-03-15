# Missing database indexes on all foreign key and filter columns

> **File:** `SampleApi/Data/AppDbContext.cs` | **Scope:** architecture

## Evidence

At `AppDbContext.cs:19-63`, `OnModelCreating` defines entity configurations for all six tables but creates **zero database indexes** beyond primary keys:

```csharp
modelBuilder.Entity<Review>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.CustomerName).HasMaxLength(100).IsRequired();
    entity.Property(e => e.Comment).HasMaxLength(2000);
    // No HasIndex on ProductId
});
```

The missing indexes and their query sites:
- **`Review.ProductId`** — `ReviewsController.cs:54` (`Where(r => r.ProductId == productId)`) and `:71–73` (CountAsync + AverageAsync)
- **`CartItem.SessionId`** — `CartController.cs:27` (`Where(c => c.SessionId == sessionId)`), `:77` (FirstOrDefaultAsync), `:147`, plus Pages/Cart `:107–109`, Pages/Checkout `:61,124`
- **`OrderItem.OrderId`** — `Pages/Orders/Index.cshtml.cs:50` (`Where(oi => orderIds.Contains(oi.OrderId))`)
- **`Product.Category`** — `ProductsController.cs:58` (`Where(p => p.Category == categoryName)`)
- **`Order.CustomerName`** — `Pages/Orders/Index.cshtml.cs:40` (`Where(o => o.CustomerName == customer)`)

The CPU profile shows **30K+ samples in TdsParser/SqlDataReader** methods and **9554 samples in UnicodeEncoding string decoding**, confirming the SQL engine reads far more data pages than necessary — consistent with table scans on filterable columns.

`Program.cs:49` uses `db.Database.EnsureCreated()` which only creates the schema on first run, so index definitions in `OnModelCreating` require either a fresh database or explicit SQL execution to apply.

## Theory

Without indexes, every server-side `Where()` clause translates to a **SQL full table scan** at the database level. Even when previous experiments (1–9) correctly moved filtering from C# to SQL, the queries still scan every row because SQL Server has no index to seek on.

The impact is worst for growing tables:
- **Orders**: starts at 100 rows, grows to **~28,000** during the load test (each VU creates 2 orders/iteration via API + Checkout)
- **OrderItems**: grows to **~56,000+** rows
- **CartItems**: ~500 active rows but with constant INSERT/DELETE churn

Under 500 concurrent VUs, table scans cause:
- Elevated logical reads (entire table per query vs. a few index pages)
- Lock contention on data pages during concurrent scans + inserts
- Buffer pool pressure forcing repeated physical I/O
- CPU time wasted on scanning non-matching rows

## Proposed Fixes

1. **Add `HasIndex()` calls in `OnModelCreating`** for all filtered columns:
   - `Review`: `.HasIndex(e => e.ProductId)` on the Review entity (line 37–42)
   - `CartItem`: `.HasIndex(e => new { e.SessionId, e.ProductId })` composite index + `.HasIndex(e => e.SessionId)` (line 58–62)
   - `OrderItem`: `.HasIndex(e => e.OrderId)` (line 52–56)
   - `Product`: `.HasIndex(e => e.Category)` (line 23–29)
   - `Order`: `.HasIndex(e => e.CustomerName)` (line 44–50)

2. **Ensure indexes apply to existing database:** Since `EnsureCreated()` is a no-op for existing databases, add raw SQL execution after the `EnsureCreated()` call in `Program.cs:49` to create indexes if they don't exist (e.g., `CREATE INDEX IF NOT EXISTS` or `db.Database.EnsureDeleted()` before `EnsureCreated()`). This makes the change span `AppDbContext.cs` + `Program.cs`, hence `architecture` scope.

## Expected Impact

- **p95 latency:** Estimated ~5–10ms average reduction across affected endpoints. The largest wins come from the growing Orders (28K+ rows) and OrderItems (56K+ rows) tables, where the Pages/Orders endpoint should see ~15–25ms improvement. CartItem lookups benefit from composite index under high INSERT/DELETE churn.
- **RPS:** ~3–5% improvement from reduced SQL Server CPU time, fewer logical reads, and lower lock contention.
- The improvement **compounds** with all previous server-side filtering optimizations (experiments 1–9) because those queries are still doing table scans without indexes.
