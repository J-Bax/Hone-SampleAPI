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
    /// NOTE: Intentionally returns ALL reviews with no pagination.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Review>>> GetReviews()
    {
        // INTENTIONAL PERF ISSUE: No pagination, returns entire table
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
    /// NOTE: Intentionally loads ALL reviews then filters in memory.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet("by-product/{productId}")]
    public async Task<ActionResult<IEnumerable<Review>>> GetReviewsByProduct(int productId)
    {
        // First verify the product exists — separate query
        var product = await _context.Products.FindAsync(productId);
        if (product == null)
            return NotFound(new { message = $"Product with ID {productId} not found" });

        // INTENTIONAL PERF ISSUE: Loads ALL reviews then filters in memory
        var allReviews = await _context.Reviews.ToListAsync();
        var filtered = allReviews.Where(r => r.ProductId == productId).ToList();

        return Ok(filtered);
    }

    /// <summary>
    /// Get average rating for a product.
    /// NOTE: Intentionally loads all reviews for the product into memory to compute average.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet("average/{productId}")]
    public async Task<ActionResult<object>> GetAverageRating(int productId)
    {
        var product = await _context.Products.FindAsync(productId);
        if (product == null)
            return NotFound(new { message = $"Product with ID {productId} not found" });

        // INTENTIONAL PERF ISSUE: Loads ALL reviews then filters in memory
        var allReviews = await _context.Reviews.ToListAsync();
        var productReviews = allReviews.Where(r => r.ProductId == productId).ToList();

        var average = productReviews.Any()
            ? Math.Round(productReviews.Average(r => r.Rating), 2)
            : 0.0;

        return Ok(new
        {
            ProductId = productId,
            AverageRating = average,
            ReviewCount = productReviews.Count
        });
    }

    /// <summary>
    /// Create a new review.
    /// NOTE: After creating, recomputes average by loading all reviews — perf target.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Review>> CreateReview(Review review)
    {
        review.CreatedAt = DateTime.UtcNow;
        _context.Reviews.Add(review);
        await _context.SaveChangesAsync();

        // INTENTIONAL PERF ISSUE: Recompute average by loading ALL reviews for the product
        var allReviews = await _context.Reviews.ToListAsync();
        var productReviews = allReviews.Where(r => r.ProductId == review.ProductId).ToList();
        var _ = productReviews.Average(r => r.Rating); // Wasted computation

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
