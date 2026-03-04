using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages;

/// <summary>
/// Home page. Loads featured products, categories, and recent reviews.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _context;

    public IndexModel(AppDbContext context)
    {
        _context = context;
    }

    public List<Product> FeaturedProducts { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public List<Review> RecentReviews { get; set; } = new();
    public int TotalProducts { get; set; }
    public int TotalCategories { get; set; }

    public async Task OnGetAsync()
    {
        var allProducts = await _context.Products.ToListAsync();
        FeaturedProducts = allProducts.OrderBy(_ => Guid.NewGuid()).Take(12).ToList();
        TotalProducts = allProducts.Count;

        // Separate query for categories
        Categories = await _context.Categories.ToListAsync();
        TotalCategories = Categories.Count;

        var allReviews = await _context.Reviews.ToListAsync();
        RecentReviews = allReviews.OrderByDescending(r => r.CreatedAt).Take(5).ToList();
    }
}
