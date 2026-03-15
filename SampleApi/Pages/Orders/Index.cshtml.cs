using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Orders;

/// <summary>
/// Order history page — looks up orders by customer name.
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

        Orders = await _context.Orders
            .Where(o => o.CustomerName == customer)
            .OrderByDescending(o => o.OrderDate)
            .ToListAsync();

        if (Orders.Count == 0)
            return;

        var orderIds = Orders.Select(o => o.Id).ToList();

        var items = await _context.OrderItems
            .Where(oi => orderIds.Contains(oi.OrderId))
            .ToListAsync();

        var productIds = items.Select(i => i.ProductId).Distinct().ToList();

        var productMap = await _context.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        foreach (var order in Orders)
        {
            var orderItems = items.Where(i => i.OrderId == order.Id).ToList();
            var itemViews = new List<OrderItemView>();

            foreach (var item in orderItems)
            {
                productMap.TryGetValue(item.ProductId, out var product);
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
