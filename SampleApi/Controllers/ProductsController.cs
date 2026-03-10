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
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Product>>> GetProducts()
    {
        var products = await _context.Products.AsNoTracking().ToListAsync();
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
    /// </summary>
    [HttpGet("by-category/{categoryName}")]
    public async Task<ActionResult<IEnumerable<Product>>> GetProductsByCategory(string categoryName)
    {
        var categoryExists = await _context.Categories.AsNoTracking()
            .AnyAsync(c => c.Name == categoryName);

        if (!categoryExists)
            return NotFound(new { message = $"Category '{categoryName}' not found" });

        var filtered = await _context.Products.AsNoTracking()
            .Where(p => p.Category == categoryName)
            .ToListAsync();

        return Ok(filtered);
    }

    /// <summary>
    /// Search products by name.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<Product>>> SearchProducts([FromQuery] string? q)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var filtered = await _context.Products.AsNoTracking()
                .Where(p => p.Name.Contains(q) || (p.Description != null && p.Description.Contains(q)))
                .ToListAsync();
            return Ok(filtered);
        }

        var allProducts = await _context.Products.AsNoTracking().ToListAsync();
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
