using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly AppDbContext _context;

    public CategoriesController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all categories.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Category>>> GetCategories()
    {
        var categories = await _context.Categories.ToListAsync();
        return Ok(categories);
    }

    /// <summary>
    /// Get a category by ID with its products.
    /// NOTE: Intentionally does NOT use .Include() — triggers lazy loading / N+1.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetCategory(int id)
    {
        var category = await _context.Categories.FindAsync(id);

        if (category == null)
            return NotFound();

        // INTENTIONAL PERF ISSUE: Separate query instead of .Include()
        var products = await _context.Products
            .Where(p => p.Category == category.Name)
            .ToListAsync();

        return Ok(new
        {
            category.Id,
            category.Name,
            category.Description,
            Products = products
        });
    }
}
