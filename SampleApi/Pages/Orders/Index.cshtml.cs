using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Orders;

/// <summary>
/// Order history page — looks up orders by customer name.
/// NOTE: Intentionally loads ALL orders then filters in memory,
/// then for each order loads ALL items and ALL products (N+1).
/// This is a performance optimization target.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _context;

    public IndexModel(AppDbContext context)
    {
        _context = context;
    }

    public string? CustomerFilter { get; set; }
    public List<Order> Orders { get; set; } = new();
    public Dictionary<int, List<OrderItemView>> OrderItemsMap { get; set; } = new();

    public class OrderItemView
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Subtotal { get; set; }
    }

    public async Task OnGetAsync(string? customer)
    {
        CustomerFilter = customer;

        if (string.IsNullOrWhiteSpace(customer))
            return;

        // INTENTIONAL PERF ISSUE: Load ALL orders then filter in memory
        var allOrders = await _context.Orders.ToListAsync();
        Orders = allOrders
            .Where(o => o.CustomerName.Equals(customer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.OrderDate)
            .ToList();

        // INTENTIONAL PERF ISSUE: For each order, load ALL items and filter
        var allItems = await _context.OrderItems.ToListAsync();

        foreach (var order in Orders)
        {
            var items = allItems.Where(i => i.OrderId == order.Id).ToList();
            var itemViews = new List<OrderItemView>();

            // INTENTIONAL PERF ISSUE: N+1 — load each product individually
            foreach (var item in items)
            {
                var product = await _context.Products.FindAsync(item.ProductId);
                itemViews.Add(new OrderItemView
                {
                    ProductId = item.ProductId,
                    ProductName = product?.Name ?? "Unknown",
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Subtotal = item.UnitPrice * item.Quantity
                });
            }

            OrderItemsMap[order.Id] = itemViews;
        }
    }
}
