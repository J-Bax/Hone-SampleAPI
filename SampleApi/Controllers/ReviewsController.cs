using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReviewsController : ControllerBase
{
    private readonly AppDbContext _context;

    public ReviewsController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all reviews.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Review>>> GetReviews()
    {
        var reviews = await _context.Reviews.ToListAsync();
        return Ok(reviews);
    }

    /// <summary>
    /// Get a single review by ID.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Review>> GetReview(int id)
    {
        var review = await _context.Reviews.FindAsync(id);

        if (review == null)
            return NotFound();

        return Ok(review);
    }

    /// <summary>
    /// Get reviews for a specific product.
    /// </summary>
    [HttpGet("by-product/{productId}")]
    public async Task<ActionResult<IEnumerable<Review>>> GetReviewsByProduct(int productId)
    {
        var filtered = await _context.Reviews.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .Select(r => new Review
            {
                Id = r.Id,
                ProductId = r.ProductId,
                CustomerName = r.CustomerName,
                Rating = r.Rating,
                CreatedAt = r.CreatedAt
            })
            .ToListAsync();

        // Common case: reviews found — product existence is implicitly proven
        if (filtered.Count > 0)
            return Ok(filtered);

        // Uncommon case: no reviews — distinguish 404 from empty list
        var productExists = await _context.Products.AsNoTracking().AnyAsync(p => p.Id == productId);
        if (!productExists)
            return NotFound(new { message = $"Product with ID {productId} not found" });

        return Ok(filtered);
    }

    /// <summary>
    /// Get average rating for a product.
    /// </summary>
    [HttpGet("average/{productId}")]
    public async Task<ActionResult<object>> GetAverageRating(int productId)
    {
        // Single aggregate query replaces three separate round trips
        var stats = await _context.Reviews
            .Where(r => r.ProductId == productId)
            .GroupBy(r => r.ProductId)
            .Select(g => new { Count = g.Count(), Average = g.Average(r => (double)r.Rating) })
            .FirstOrDefaultAsync();

        // Common case: reviews found — product existence is implicitly proven
        if (stats != null)
        {
            return Ok(new
            {
                ProductId = productId,
                AverageRating = Math.Round(stats.Average, 2),
                ReviewCount = stats.Count
            });
        }

        // Uncommon case: no reviews — distinguish 404 from zero-review product
        var productExists = await _context.Products.AsNoTracking().AnyAsync(p => p.Id == productId);
        if (!productExists)
            return NotFound(new { message = $"Product with ID {productId} not found" });

        return Ok(new
        {
            ProductId = productId,
            AverageRating = 0.0,
            ReviewCount = 0
        });
    }

    /// <summary>
    /// Create a new review.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Review>> CreateReview(Review review)
    {
        review.CreatedAt = DateTime.UtcNow;
        _context.Reviews.Add(review);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetReview), new { id = review.Id }, review);
    }

    /// <summary>
    /// Delete a review.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteReview(int id)
    {
        var review = await _context.Reviews.FindAsync(id);
        if (review == null)
            return NotFound();

        _context.Reviews.Remove(review);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
