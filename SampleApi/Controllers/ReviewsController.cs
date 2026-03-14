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
        // First verify the product exists — separate query
        var product = await _context.Products.FindAsync(productId);
        if (product == null)
            return NotFound(new { message = $"Product with ID {productId} not found" });

        var filtered = await _context.Reviews.Where(r => r.ProductId == productId).ToListAsync();

        return Ok(filtered);
    }

    /// <summary>
    /// Get average rating for a product.
    /// </summary>
    [HttpGet("average/{productId}")]
    public async Task<ActionResult<object>> GetAverageRating(int productId)
    {
        var product = await _context.Products.FindAsync(productId);
        if (product == null)
            return NotFound(new { message = $"Product with ID {productId} not found" });

        var productReviewsQuery = _context.Reviews.Where(r => r.ProductId == productId);
        var reviewCount = await productReviewsQuery.CountAsync();
        var average = reviewCount > 0
            ? Math.Round(await productReviewsQuery.AverageAsync(r => r.Rating), 2)
            : 0.0;

        return Ok(new
        {
            ProductId = productId,
            AverageRating = average,
            ReviewCount = reviewCount
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
