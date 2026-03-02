using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Products;

/// <summary>
/// Product detail page — shows product info, reviews, and related products.
/// NOTE: Intentionally fires separate queries for product, reviews, and related products.
/// Each query loads its entire table and filters in memory.
/// This is a performance optimization target.
/// </summary>
public class DetailModel : PageModel
{
    private readonly AppDbContext _context;

    public DetailModel(AppDbContext context)
    {
        _context = context;
    }

    public Product? Product { get; set; }
    public List<Review> Reviews { get; set; } = new();
    public List<Product> RelatedProducts { get; set; } = new();
    public double AverageRating { get; set; }
    public string? CartMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        // Query 1: Load the product
        Product = await _context.Products.FindAsync(id);
        if (Product == null)
            return Page();

        // INTENTIONAL PERF ISSUE: Query 2 — loads ALL reviews then filters
        var allReviews = await _context.Reviews.ToListAsync();
        Reviews = allReviews.Where(r => r.ProductId == id)
                            .OrderByDescending(r => r.CreatedAt)
                            .ToList();

        AverageRating = Reviews.Any() ? Math.Round(Reviews.Average(r => r.Rating), 1) : 0;

        // INTENTIONAL PERF ISSUE: Query 3 — loads ALL products then filters for related
        var allProducts = await _context.Products.ToListAsync();
        RelatedProducts = allProducts
            .Where(p => p.Category == Product.Category && p.Id != id)
            .OrderBy(_ => Guid.NewGuid())
            .Take(4)
            .ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, int productId, int quantity = 1)
    {
        // Get or create session ID from cookie
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

        // INTENTIONAL PERF ISSUE: Load ALL cart items to find existing
        var allCartItems = await _context.CartItems.ToListAsync();
        var existing = allCartItems.FirstOrDefault(c =>
            c.SessionId == sessionId && c.ProductId == productId);

        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            _context.CartItems.Add(new CartItem
            {
                SessionId = sessionId,
                ProductId = productId,
                Quantity = quantity,
                AddedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
        CartMessage = "Item added to cart!";

        // Re-load page data
        return await OnGetAsync(productId);
    }
}
