using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _context;

    public ProductsController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all products.
    /// NOTE: Intentionally returns ALL products with no pagination.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Product>>> GetProducts()
    {
        // INTENTIONAL PERF ISSUE: No pagination, returns entire table
        var products = await _context.Products.ToListAsync();
        return Ok(products);
    }

    /// <summary>
    /// Get a single product by ID.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetProduct(int id)
    {
        var product = await _context.Products.FindAsync(id);

        if (product == null)
            return NotFound();

        return Ok(product);
    }

    /// <summary>
    /// Get products by category.
    /// NOTE: Intentionally uses N+1 pattern — fetches all categories,
    /// then queries products one-by-one per category match.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet("by-category/{categoryName}")]
    public async Task<ActionResult<IEnumerable<Product>>> GetProductsByCategory(string categoryName)
    {
        // INTENTIONAL PERF ISSUE: N+1 query pattern
        // First, get all categories to find the matching one
        var categories = await _context.Categories.ToListAsync();
        var matchingCategory = categories.FirstOrDefault(c =>
            c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

        if (matchingCategory == null)
            return NotFound(new { message = $"Category '{categoryName}' not found" });

        // INTENTIONAL PERF ISSUE: Loads ALL products then filters in memory
        var allProducts = await _context.Products.ToListAsync();
        var filtered = allProducts.Where(p =>
            p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();

        return Ok(filtered);
    }

    /// <summary>
    /// Search products by name.
    /// NOTE: Intentionally loads all products then filters in memory.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<Product>>> SearchProducts([FromQuery] string? q)
    {
        // INTENTIONAL PERF ISSUE: Loads all products, filters in memory
        var allProducts = await _context.Products.ToListAsync();

        if (!string.IsNullOrWhiteSpace(q))
        {
            allProducts = allProducts.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (p.Description != null && p.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        return Ok(allProducts);
    }

    /// <summary>
    /// Create a new product.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Product>> CreateProduct(Product product)
    {
        product.CreatedAt = DateTime.UtcNow;
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }

    /// <summary>
    /// Update an existing product.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProduct(int id, Product product)
    {
        if (id != product.Id)
            return BadRequest(new { message = "ID mismatch" });

        var existing = await _context.Products.FindAsync(id);
        if (existing == null)
            return NotFound();

        existing.Name = product.Name;
        existing.Description = product.Description;
        existing.Price = product.Price;
        existing.Category = product.Category;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// Delete a product.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProduct(int id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null)
            return NotFound();

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
