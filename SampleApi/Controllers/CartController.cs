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
    /// </summary>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<object>> GetCart(string sessionId)
    {
        var sessionItems = await _context.CartItems
            .AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .ToListAsync();

        var productIds = sessionItems.Select(c => c.ProductId).ToList();
        var products = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Price })
            .ToDictionaryAsync(p => p.Id);

        var cartDetails = new List<object>();
        decimal total = 0m;

        foreach (var item in sessionItems)
        {
            products.TryGetValue(item.ProductId, out var product);
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

        var existing = await _context.CartItems.FirstOrDefaultAsync(c =>
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
    /// </summary>
    [HttpDelete("session/{sessionId}")]
    public async Task<IActionResult> ClearCart(string sessionId)
    {
        var sessionItems = await _context.CartItems
            .Where(c => c.SessionId == sessionId)
            .ToListAsync();

        _context.CartItems.RemoveRange(sessionItems);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
