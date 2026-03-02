using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CartController : ControllerBase
{
    private readonly AppDbContext _context;

    public CartController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get cart contents for a session.
    /// NOTE: Intentionally loads ALL cart items then filters, plus N+1 for product details.
    /// This is a performance optimization target.
    /// </summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<object>> GetCart(string sessionId)
    {
        // INTENTIONAL PERF ISSUE: Loads ALL cart items then filters in memory
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

        // INTENTIONAL PERF ISSUE: N+1 — load each product individually
        var cartDetails = new List<object>();
        decimal total = 0m;

        foreach (var item in sessionItems)
        {
            var product = await _context.Products.FindAsync(item.ProductId);
            var subtotal = (product?.Price ?? 0m) * item.Quantity;
            total += subtotal;

            cartDetails.Add(new
            {
                item.Id,
                item.ProductId,
                ProductName = product?.Name ?? "Unknown",
                ProductPrice = product?.Price ?? 0m,
                item.Quantity,
                Subtotal = subtotal,
                item.AddedAt
            });
        }

        return Ok(new
        {
            SessionId = sessionId,
            Items = cartDetails,
            ItemCount = cartDetails.Count,
            Total = Math.Round(total, 2)
        });
    }

    /// <summary>
    /// Add an item to the cart.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CartItem>> AddToCart(AddToCartRequest request)
    {
        // Check if product exists
        var product = await _context.Products.FindAsync(request.ProductId);
        if (product == null)
            return NotFound(new { message = $"Product with ID {request.ProductId} not found" });

        // INTENTIONAL PERF ISSUE: Load all cart items to check for existing
        var allItems = await _context.CartItems.ToListAsync();
        var existing = allItems.FirstOrDefault(c =>
            c.SessionId == request.SessionId && c.ProductId == request.ProductId);

        if (existing != null)
        {
            // Item already in cart — increment quantity
            existing.Quantity += request.Quantity;
            await _context.SaveChangesAsync();
            return Ok(existing);
        }

        var cartItem = new CartItem
        {
            SessionId = request.SessionId,
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            AddedAt = DateTime.UtcNow
        };

        _context.CartItems.Add(cartItem);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetCart), new { sessionId = request.SessionId }, cartItem);
    }

    /// <summary>
    /// Update cart item quantity.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCartItem(int id, [FromBody] int quantity)
    {
        var item = await _context.CartItems.FindAsync(id);
        if (item == null)
            return NotFound();

        if (quantity <= 0)
        {
            _context.CartItems.Remove(item);
        }
        else
        {
            item.Quantity = quantity;
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Remove a single item from the cart.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> RemoveCartItem(int id)
    {
        var item = await _context.CartItems.FindAsync(id);
        if (item == null)
            return NotFound();

        _context.CartItems.Remove(item);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// Clear all items from a session's cart.
    /// NOTE: Intentionally loads ALL cart items, filters in memory,
    /// then removes one-by-one instead of bulk delete.
    /// This is a performance optimization target.
    /// </summary>
    [HttpDelete("session/{sessionId}")]
    public async Task<IActionResult> ClearCart(string sessionId)
    {
        // INTENTIONAL PERF ISSUE: Load ALL cart items, filter in memory
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

        // INTENTIONAL PERF ISSUE: Remove one-by-one instead of bulk delete
        foreach (var item in sessionItems)
        {
            _context.CartItems.Remove(item);
            await _context.SaveChangesAsync(); // Saves each time — extra round trips
        }

        return NoContent();
    }
}
