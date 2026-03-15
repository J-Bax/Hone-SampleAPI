using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Cart;

/// <summary>
/// Cart page — displays cart contents with product details.
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

        var sessionItems = await _context.CartItems
            .Where(c => c.SessionId == sessionId)
            .ToListAsync();

        _context.CartItems.RemoveRange(sessionItems);
        await _context.SaveChangesAsync();

        Message = "Cart cleared.";
        await LoadCart();
        return Page();
    }

    private async Task LoadCart()
    {
        var sessionId = GetSessionId();

        var sessionItems = await _context.CartItems
            .AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .ToListAsync();

        CartItems = new List<CartItemViewModel>();
        Total = 0m;

        if (sessionItems.Count > 0)
        {
            var productIds = sessionItems.Select(i => i.ProductId).Distinct().ToList();
            var products = await _context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id);

            foreach (var item in sessionItems)
            {
                products.TryGetValue(item.ProductId, out var product);
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
        }

        Total = Math.Round(Total, 2);
    }
}
