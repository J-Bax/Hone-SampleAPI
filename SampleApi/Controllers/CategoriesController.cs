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
        var categories = await _context.Categories.AsNoTracking().ToListAsync();
        return Ok(categories);
    }

    /// <summary>
    /// Get a category by ID with its products.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetCategory(int id)
    {
        var category = await _context.Categories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
            return NotFound();

        var products = await _context.Products.AsNoTracking()
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
