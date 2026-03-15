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

    private static List<Product>? _cachedProducts;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

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
        if (_cachedProducts != null && DateTime.UtcNow < _cacheExpiry)
            return Ok(_cachedProducts);

        await _cacheLock.WaitAsync();
        try
        {
            if (_cachedProducts != null && DateTime.UtcNow < _cacheExpiry)
                return Ok(_cachedProducts);

            var products = await _context.Products.AsNoTracking().ToListAsync();
            _cachedProducts = products;
            _cacheExpiry = DateTime.UtcNow.Add(_cacheTtl);
            return Ok(_cachedProducts);
        }
        finally
        {
            _cacheLock.Release();
        }
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
        var cached = await GetOrPopulateCacheAsync();

        var lowerCategory = categoryName.ToLower();
        var filtered = cached.Where(p => p.Category.ToLower() == lowerCategory).ToList();

        if (filtered.Count == 0)
        {
            var categoryExists = await _context.Categories
                .AnyAsync(c => c.Name.ToLower() == lowerCategory);

            if (!categoryExists)
                return NotFound(new { message = $"Category '{categoryName}' not found" });
        }

        return Ok(filtered);
    }

    /// <summary>
    /// Search products by name.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<Product>>> SearchProducts([FromQuery] string? q)
    {
        var cached = await GetOrPopulateCacheAsync();

        if (string.IsNullOrWhiteSpace(q))
            return Ok(cached);

        var lowerQ = q.ToLower();
        var results = cached
            .Where(p => p.Name.ToLower().Contains(lowerQ) ||
                        (p.Description != null && p.Description.ToLower().Contains(lowerQ)))
            .ToList();

        return Ok(results);
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

    private async Task<List<Product>> GetOrPopulateCacheAsync()
    {
        if (_cachedProducts != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedProducts;

        await _cacheLock.WaitAsync();
        try
        {
            if (_cachedProducts != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedProducts;

            var products = await _context.Products.AsNoTracking().ToListAsync();
            _cachedProducts = products;
            _cacheExpiry = DateTime.UtcNow.Add(_cacheTtl);
            return _cachedProducts;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
