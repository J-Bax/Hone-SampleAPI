using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Pages.Products;

/// <summary>
/// Product browsing page with optional category filter and search.
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

        IQueryable<Product> query = _context.Products;

        // Server-side category filter
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(p => p.Category == category);
        }

        // Server-side search filter
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(p =>
                p.Name.Contains(q) ||
                (p.Description != null && p.Description.Contains(q)));
        }

        var totalCount = await query.CountAsync();
        TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize);
        if (TotalPages < 1) TotalPages = 1;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        Products = await query
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .AsNoTracking()
            .Select(p => new Product
            {
                Id = p.Id,
                Name = p.Name,
                Price = p.Price,
                Category = p.Category,
                Stock = p.Stock,
                ImageUrl = p.ImageUrl
            })
            .ToListAsync();

        // Separate query for categories sidebar
        Categories = await _context.Categories.AsNoTracking().ToListAsync();
    }
}
