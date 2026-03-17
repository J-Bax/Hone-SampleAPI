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
        TotalProducts = await _context.Products.CountAsync();
        var offset = Random.Shared.Next(Math.Max(1, TotalProducts - 12));
        FeaturedProducts = await _context.Products.AsNoTracking()
            .OrderBy(p => p.Id)
            .Skip(offset)
            .Take(12)
            .Select(p => new Product { Id = p.Id, Name = p.Name, Price = p.Price, Category = p.Category, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt })
            .ToListAsync();

        // Separate query for categories
        Categories = await _context.Categories.AsNoTracking().ToListAsync();
        TotalCategories = Categories.Count;

        RecentReviews = await _context.Reviews.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(5)
            .Select(r => new Review { Id = r.Id, ProductId = r.ProductId, CustomerName = r.CustomerName, Rating = r.Rating, CreatedAt = r.CreatedAt })
            .ToListAsync();
    }
}
