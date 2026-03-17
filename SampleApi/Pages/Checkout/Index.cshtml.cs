using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Checkout;

/// <summary>
/// Checkout page — shows order summary and places order from cart.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _context;

    public IndexModel(AppDbContext context)
    {
        _context = context;
    }

    public List<CartItemView> CartItems { get; set; } = new();
    public decimal Total { get; set; }
    public bool OrderPlaced { get; set; }
    public int OrderId { get; set; }
    public decimal OrderTotal { get; set; }
    public string? CustomerName { get; set; }

    public class CartItemView
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal ProductPrice { get; set; }
        public int Quantity { get; set; }
        public decimal Subtotal { get; set; }
    }

    private string GetSessionId()
    {
        var sessionId = Request.Cookies["CartSessionId"];
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
            Response.Cookies.Append("CartSessionId", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Expires = DateTimeOffset.UtcNow.AddDays(30)
            });
        }
        return sessionId;
    }

    public async Task OnGetAsync()
    {
        await LoadCartSummary();
    }

    public async Task<IActionResult> OnPostAsync(string customerName)
    {
        var sessionId = GetSessionId();

        var cartWithProducts = await _context.CartItems
            .AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .Join(
                _context.Products.AsNoTracking(),
                c => c.ProductId,
                p => p.Id,
                (c, p) => new { c.ProductId, c.Quantity, p.Price })
            .ToListAsync();

        if (!cartWithProducts.Any())
        {
            await LoadCartSummary();
            return Page();
        }

        // Create order
        var order = new Order
        {
            CustomerName = customerName,
            OrderDate = DateTime.UtcNow,
            Status = "Pending",
            TotalAmount = 0m
        };

        _context.Orders.Add(order);

        decimal total = 0m;

        foreach (var item in cartWithProducts)
        {
            _context.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = item.Price
            });

            total += item.Price * item.Quantity;
        }

        order.TotalAmount = Math.Round(total, 2);
        await _context.SaveChangesAsync(); // Save order, order items, and updated total
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM CartItems WHERE SessionId = {sessionId}");

        OrderPlaced = true;
        OrderId = order.Id;
        OrderTotal = order.TotalAmount;
        CustomerName = customerName;

        return Page();
    }

    private async Task LoadCartSummary()
    {
        var sessionId = GetSessionId();

        var cartWithProducts = await _context.CartItems
            .AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .Join(
                _context.Products.AsNoTracking(),
                c => c.ProductId,
                p => p.Id,
                (c, p) => new { c.ProductId, p.Name, p.Price, c.Quantity })
            .ToListAsync();

        CartItems = new List<CartItemView>();
        Total = 0m;

        foreach (var item in cartWithProducts)
        {
            var subtotal = item.Price * item.Quantity;
            Total += subtotal;

            CartItems.Add(new CartItemView
            {
                ProductId = item.ProductId,
                ProductName = item.Name,
                ProductPrice = item.Price,
                Quantity = item.Quantity,
                Subtotal = subtotal
            });
        }

        Total = Math.Round(Total, 2);
    }
}
