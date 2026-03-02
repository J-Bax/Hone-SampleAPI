using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Checkout;

/// <summary>
/// Checkout page — shows order summary and places order from cart.
/// NOTE: Intentionally uses N+1 queries for product lookups and
/// saves each order item individually.
/// This is a performance optimization target.
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

        // INTENTIONAL PERF ISSUE: Load ALL cart items, filter in memory
        var allCartItems = await _context.CartItems.ToListAsync();
        var sessionItems = allCartItems.Where(c => c.SessionId == sessionId).ToList();

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

        decimal total = 0m;

        // INTENTIONAL PERF ISSUE: N+1 — look up each product individually
        foreach (var cartItem in sessionItems)
        {
            var product = await _context.Products.FindAsync(cartItem.ProductId);
            var price = product?.Price ?? 0m;

            _context.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id,
                ProductId = cartItem.ProductId,
                Quantity = cartItem.Quantity,
                UnitPrice = price
            });

            total += price * cartItem.Quantity;

            // INTENTIONAL PERF ISSUE: Save each item individually
            await _context.SaveChangesAsync();
        }

        order.TotalAmount = Math.Round(total, 2);
        await _context.SaveChangesAsync();

        // Clear cart — one by one
        foreach (var cartItem in sessionItems)
        {
            _context.CartItems.Remove(cartItem);
            await _context.SaveChangesAsync();
        }

        OrderPlaced = true;
        OrderId = order.Id;
        OrderTotal = order.TotalAmount;
        CustomerName = customerName;

        return Page();
    }

    private async Task LoadCartSummary()
    {
        var sessionId = GetSessionId();

        // INTENTIONAL PERF ISSUE: Load ALL cart items, filter in memory
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

        CartItems = new List<CartItemView>();
        Total = 0m;

        // INTENTIONAL PERF ISSUE: N+1 product lookups
        foreach (var item in sessionItems)
        {
            var product = await _context.Products.FindAsync(item.ProductId);
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
