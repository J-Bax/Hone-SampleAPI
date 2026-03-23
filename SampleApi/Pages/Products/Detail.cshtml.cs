using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Products;

/// <summary>
/// Product detail page — shows product info, reviews, and related products.
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
        Product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (Product == null)
            return Page();

        Reviews = await _context.Reviews
            .AsNoTracking()
            .Where(r => r.ProductId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new Review { Id = r.Id, ProductId = r.ProductId, CustomerName = r.CustomerName, Rating = r.Rating, CreatedAt = r.CreatedAt })
            .ToListAsync();

        AverageRating = Reviews.Any() ? Math.Round(Reviews.Average(r => r.Rating), 1) : 0;

        RelatedProducts = await _context.Products
            .AsNoTracking()
            .Where(p => p.Category == Product.Category && p.Id != id)
            .Take(4)
            .Select(p => new Product { Id = p.Id, Name = p.Name, Price = p.Price, Category = p.Category })
            .ToListAsync();

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

        // Load product and page data upfront so we don't re-run these queries
        // after the cart operation via OnGetAsync.
        var product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId);

        var existing = await _context.CartItems.FirstOrDefaultAsync(c =>
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

        // Populate view model directly from already-loaded data instead of
        // calling OnGetAsync, which would re-execute 3 additional DB queries.
        Product = product;
        if (Product != null)
        {
            Reviews = await _context.Reviews
                .AsNoTracking()
                .Where(r => r.ProductId == productId)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new Review { Id = r.Id, ProductId = r.ProductId, CustomerName = r.CustomerName, Rating = r.Rating, CreatedAt = r.CreatedAt })
                .ToListAsync();

            AverageRating = Reviews.Any() ? Math.Round(Reviews.Average(r => r.Rating), 1) : 0;

            RelatedProducts = await _context.Products
                .AsNoTracking()
                .Where(p => p.Category == Product.Category && p.Id != productId)
                .Take(4)
                .Select(p => new Product { Id = p.Id, Name = p.Name, Price = p.Price, Category = p.Category })
                .ToListAsync();
        }

        return Page();
    }
}
