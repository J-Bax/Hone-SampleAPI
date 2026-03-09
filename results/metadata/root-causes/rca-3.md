# GetOrder has N+1 query loop; GetOrdersByCustomer loads all orders client-side

> **File:** `SampleApi/Controllers/OrdersController.cs` | **Scope:** narrow

## Evidence

**`GetOrder` (lines 52-58) — N+1 query pattern:**
```csharp
var allItems = await _context.OrderItems.ToListAsync();
var items = allItems.Where(i => i.OrderId == id).ToList();

var itemDetails = new List<object>();
foreach (var item in items)
{
    var product = await _context.Products.FindAsync(item.ProductId);
```
First loads ALL order items into memory (line 52), filters client-side (line 53), then issues a separate `FindAsync` for each item's product (line 58). For an order with N items, this is 1 (all order items) + N (product lookups) queries.

**`GetOrdersByCustomer` (lines 35-37):**
```csharp
var allOrders = await _context.Orders.ToListAsync();
var filtered = allOrders.Where(o =>
    o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();
```
Loads all orders then filters in memory.

**`CreateOrder` (lines 103-107) — sequential FindAsync per item:**
```csharp
foreach (var itemReq in request.Items)
{
    var product = await _context.Products.FindAsync(itemReq.ProductId);
```
Each order item triggers a separate DB round-trip. The baseline sends 2 items per order (`baseline.js` lines 97-100), so this is 2 extra queries per VU iteration.

`GetOrder` and `GetOrdersByCustomer` are not directly hit by the baseline scenario, but `CreateOrder` is called every iteration. As the Orders and OrderItems tables grow during the test, `GetOrder`'s full table scan of OrderItems becomes increasingly expensive if called.

## Proposed Fixes

1. **Fix GetOrder N+1:** Replace the full table load + loop with a single query using `Include` or a projection join: `_context.OrderItems.Where(oi => oi.OrderId == id).Join(_context.Products, oi => oi.ProductId, p => p.Id, ...)`. This collapses N+1 queries into 1.

2. **Fix GetOrdersByCustomer:** Replace `ToListAsync()` + client-side Where with `_context.Orders.Where(o => o.CustomerName == customerName).ToListAsync()`. Add an index on `Order.CustomerName` in `AppDbContext.OnModelCreating` (line 44-49).

3. **Fix CreateOrder:** Batch-load products with `_context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id)` before the loop instead of individual `FindAsync` calls.

## Expected Impact

- **p95 latency:** ~5-10% reduction. `CreateOrder` is called once per VU iteration with 2 items, so eliminating 2 serial round-trips saves ~2-4ms per request. The `GetOrder` and `GetOrdersByCustomer` fixes prevent future degradation.
- **RPS:** ~5% increase from reduced DB round-trips on the write path.
- **Error rate:** No change expected.
