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
        FeaturedProducts = await _context.Products.AsNoTracking().OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync();
        TotalProducts = await _context.Products.CountAsync();

        // Separate query for categories
        Categories = await _context.Categories.AsNoTracking().ToListAsync();
        TotalCategories = Categories.Count;

        RecentReviews = await _context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync();
    }
}
