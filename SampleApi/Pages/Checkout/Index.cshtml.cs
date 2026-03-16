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

        var sessionItems = await _context.CartItems
            .Where(c => c.SessionId == sessionId)
            .ToListAsync();

        if (!sessionItems.Any())
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
        await _context.SaveChangesAsync(); // Save to get ID

        var productIds = sessionItems.Select(c => c.ProductId).ToList();
        var products = await _context.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        decimal total = 0m;

        foreach (var cartItem in sessionItems)
        {
            products.TryGetValue(cartItem.ProductId, out var product);
            var price = product?.Price ?? 0m;

            _context.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id,
                ProductId = cartItem.ProductId,
                Quantity = cartItem.Quantity,
                UnitPrice = price
            });

            total += price * cartItem.Quantity;
        }

        order.TotalAmount = Math.Round(total, 2);
        _context.CartItems.RemoveRange(sessionItems);
        await _context.SaveChangesAsync(); // Save order items, update total, and clear cart atomically

        OrderPlaced = true;
        OrderId = order.Id;
        OrderTotal = order.TotalAmount;
        CustomerName = customerName;

        return Page();
    }

    private async Task LoadCartSummary()
    {
        var sessionId = GetSessionId();

        var sessionItems = await _context.CartItems
            .Where(c => c.SessionId == sessionId)
            .ToListAsync();

        CartItems = new List<CartItemView>();
        Total = 0m;

        var productIds = sessionItems.Select(i => i.ProductId).ToList();
        var products = await _context.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        foreach (var item in sessionItems)
        {
            products.TryGetValue(item.ProductId, out var product);
            var subtotal = (product?.Price ?? 0m) * item.Quantity;
            Total += subtotal;

            CartItems.Add(new CartItemView
            {
                ProductId = item.ProductId,
                ProductName = product?.Name ?? "Unknown",
                ProductPrice = product?.Price ?? 0m,
                Quantity = item.Quantity,
                Subtotal = subtotal
            });
        }

        Total = Math.Round(Total, 2);
    }
}
