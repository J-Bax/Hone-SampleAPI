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

    private static List<Product> _cachedFeaturedProducts = new();
    private static DateTime _featuredProductsCacheExpiry = DateTime.MinValue;
    private static readonly object _featuredProductsLock = new();
    private static int _cachedProductCount;

    private static List<Category> _cachedCategories = new();
    private static DateTime _categoriesCacheExpiry = DateTime.MinValue;
    private static readonly object _categoriesLock = new();

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
        if (DateTime.UtcNow < _featuredProductsCacheExpiry)
        {
            FeaturedProducts = _cachedFeaturedProducts;
            TotalProducts = _cachedProductCount;
        }
        else
        {
            var fresh = await _context.Products.AsNoTracking().OrderBy(p => EF.Functions.Random()).Take(12).ToListAsync();
            var count = await _context.Products.CountAsync();
            lock (_featuredProductsLock)
            {
                if (DateTime.UtcNow >= _featuredProductsCacheExpiry)
                {
                    _cachedFeaturedProducts = fresh;
                    _cachedProductCount = count;
                    _featuredProductsCacheExpiry = DateTime.UtcNow.AddSeconds(30);
                }
            }
            FeaturedProducts = fresh;
            TotalProducts = count;
        }

        if (DateTime.UtcNow < _categoriesCacheExpiry)
        {
            Categories = _cachedCategories;
        }
        else
        {
            var freshCategories = await _context.Categories.AsNoTracking().ToListAsync();
            lock (_categoriesLock)
            {
                if (DateTime.UtcNow >= _categoriesCacheExpiry)
                {
                    _cachedCategories = freshCategories;
                    _categoriesCacheExpiry = DateTime.UtcNow.AddSeconds(60);
                }
            }
            Categories = freshCategories;
        }
        TotalCategories = Categories.Count;

        RecentReviews = await _context.Reviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync();
    }
}
