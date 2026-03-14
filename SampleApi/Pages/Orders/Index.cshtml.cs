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

        var orderIds = Orders.Select(o => o.Id).ToHashSet();

        var itemViews = await _context.OrderItems
            .AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .Join(
                _context.Products.AsNoTracking(),
                i => i.ProductId,
                p => p.Id,
                (i, p) => new
                {
                    i.OrderId,
                    i.ProductId,
                    ProductName = p.Name,
                    i.Quantity,
                    i.UnitPrice
                })
            .ToListAsync();

        OrderItemsMap = itemViews
            .GroupBy(x => x.OrderId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new OrderItemView
                {
                    ProductId = x.ProductId,
                    ProductName = x.ProductName,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    Subtotal = x.UnitPrice * x.Quantity
                }).ToList());
    }
}
