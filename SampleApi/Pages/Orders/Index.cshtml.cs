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
            .AsNoTracking()
            .Where(o => o.CustomerName == customer)
            .OrderByDescending(o => o.OrderDate)
            .ToListAsync();

        if (Orders.Count == 0)
            return;

        var orderIds = Orders.Select(o => o.Id).ToList();

        var items = await _context.OrderItems
            .AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .ToListAsync();

        var productIds = items.Select(i => i.ProductId).Distinct().ToList();

        var productMap = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        foreach (var order in Orders)
        {
            var itemViews = items
                .Where(i => i.OrderId == order.Id)
                .Select(i =>
                {
                    productMap.TryGetValue(i.ProductId, out var product);
                    return new OrderItemView
                    {
                        ProductId = i.ProductId,
                        ProductName = product?.Name ?? "Unknown",
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        Subtotal = i.UnitPrice * i.Quantity
                    };
                })
                .ToList();

            OrderItemsMap[order.Id] = itemViews;
        }
    }
}
