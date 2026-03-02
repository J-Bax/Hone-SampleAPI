using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Cart;

/// <summary>
/// Cart page — displays cart contents with product details.
/// NOTE: Intentionally loads ALL cart items then filters, plus N+1 for product lookups.
/// This is a performance optimization target.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _context;

    public IndexModel(AppDbContext context)
    {
        _context = context;
    }

    public List<CartItemViewModel> CartItems { get; set; } = new();
    public decimal Total { get; set; }
    public string? Message { get; set; }

    public class CartItemViewModel
    {
        public int CartItemId { get; set; }
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
        await LoadCart();
    }

    public async Task<IActionResult> OnPostUpdateQuantityAsync(int cartItemId, int quantity)
    {
        var item = await _context.CartItems.FindAsync(cartItemId);
        if (item != null)
        {
            if (quantity <= 0)
                _context.CartItems.Remove(item);
            else
                item.Quantity = quantity;

            await _context.SaveChangesAsync();
        }

        Message = "Cart updated.";
        await LoadCart();
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int cartItemId)
    {
        var item = await _context.CartItems.FindAsync(cartItemId);
        if (item != null)
        {
            _context.CartItems.Remove(item);
            await _context.SaveChangesAsync();
        }

        Message = "Item removed.";
        await LoadCart();
        return Page();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        var sessionId = GetSessionId();

        // INTENTIONAL PERF ISSUE: Load ALL cart items, filter in memory, delete one-by-one
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

        foreach (var item in sessionItems)
        {
            _context.CartItems.Remove(item);
            await _context.SaveChangesAsync();
        }

        Message = "Cart cleared.";
        await LoadCart();
        return Page();
    }

    private async Task LoadCart()
    {
        var sessionId = GetSessionId();

        // INTENTIONAL PERF ISSUE: Load ALL cart items into memory then filter
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

        CartItems = new List<CartItemViewModel>();
        Total = 0m;

        // INTENTIONAL PERF ISSUE: N+1 — load each product individually
        foreach (var item in sessionItems)
        {
            var product = await _context.Products.FindAsync(item.ProductId);
            var subtotal = (product?.Price ?? 0m) * item.Quantity;
            Total += subtotal;

            CartItems.Add(new CartItemViewModel
            {
                CartItemId = item.Id,
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
