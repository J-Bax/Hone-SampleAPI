using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Products;

/// <summary>
/// Product browsing page with optional category filter and search.
/// NOTE: Intentionally loads ALL products then filters/paginates in memory.
/// This is a performance optimization target.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _context;
    private const int PageSize = 24;

    public IndexModel(AppDbContext context)
    {
        _context = context;
    }

    public List<Product> Products { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public string? SelectedCategory { get; set; }
    public string? SearchQuery { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; } = 1;

    public async Task OnGetAsync(string? category, string? q, int page = 1)
    {
        SelectedCategory = category;
        SearchQuery = q;
        CurrentPage = page < 1 ? 1 : page;

        // INTENTIONAL PERF ISSUE: Loads ALL products into memory
        var allProducts = await _context.Products.ToListAsync();

        // Filter by category in memory
        if (!string.IsNullOrWhiteSpace(category))
        {
            allProducts = allProducts.Where(p =>
                p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Filter by search in memory
        if (!string.IsNullOrWhiteSpace(q))
        {
            allProducts = allProducts.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (p.Description != null && p.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        TotalPages = (int)Math.Ceiling(allProducts.Count / (double)PageSize);
        if (TotalPages < 1) TotalPages = 1;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        // INTENTIONAL PERF ISSUE: In-memory pagination
        Products = allProducts
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        // Separate query for categories sidebar
        Categories = await _context.Categories.ToListAsync();
    }
}
