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
        // Fetch count first so we can use it for both TotalProducts and random sampling.
        TotalProducts = await _context.Products.CountAsync();

        // Replace ORDER BY NEWID() with a single indexed PK seek:
        // generate a random start offset and return 12 consecutive rows via the clustered index.
        // If fewer than 12 products remain above the random offset, wrap around from the beginning.
        int randomStart = TotalProducts > 0 ? Random.Shared.Next(0, TotalProducts) : 0;
        FeaturedProducts = await _context.Products.AsNoTracking()
            .OrderBy(p => p.Id)
            .Skip(randomStart)
            .Take(12)
            .ToListAsync();

        if (FeaturedProducts.Count < 12 && TotalProducts >= 12)
        {
            int needed = 12 - FeaturedProducts.Count;
            var wrapped = await _context.Products.AsNoTracking()
                .OrderBy(p => p.Id)
                .Take(needed)
                .ToListAsync();
            FeaturedProducts.AddRange(wrapped);
        }

        // Separate query for categories
        Categories = await _context.Categories.AsNoTracking().ToListAsync();
        TotalCategories = Categories.Count;

        RecentReviews = await _context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync();
    }
}
