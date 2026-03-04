Analyze this Web API's performance and identify the single highest-impact optimization.

## Current Performance (Iteration 1)
- p95 Latency: 88.637805ms
- Requests/sec: 115
- Error rate: 0%
- Improvement vs baseline: 0%

## Baseline Performance
- p95 Latency: 88.637805ms
- Requests/sec: 115
- Error rate: 0%



## Source Code
// === CartController.cs ===
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
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

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
    /// </summary>
    [HttpDelete("session/{sessionId}")]
    public async Task<IActionResult> ClearCart(string sessionId)
    {
        var allItems = await _context.CartItems.ToListAsync();
        var sessionItems = allItems.Where(c => c.SessionId == sessionId).ToList();

        foreach (var item in sessionItems)
        {
            _context.CartItems.Remove(item);
            await _context.SaveChangesAsync(); // Saves each time — extra round trips
        }

        return NoContent();
    }
}


// === CategoriesController.cs ===
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
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetCategory(int id)
    {
        var category = await _context.Categories.FindAsync(id);

        if (category == null)
            return NotFound();

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


// === OrdersController.cs ===
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _context;

    public OrdersController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all orders.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Order>>> GetOrders()
    {
        var orders = await _context.Orders.ToListAsync();
        return Ok(orders);
    }

    /// <summary>
    /// Get orders by customer name.
    /// </summary>
    [HttpGet("by-customer/{customerName}")]
    public async Task<ActionResult<IEnumerable<Order>>> GetOrdersByCustomer(string customerName)
    {
        var allOrders = await _context.Orders.ToListAsync();
        var filtered = allOrders.Where(o =>
            o.CustomerName.Equals(customerName, StringComparison.OrdinalIgnoreCase)).ToList();

        return Ok(filtered);
    }

    /// <summary>
    /// Get order by ID with its items and product details.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetOrder(int id)
    {
        var order = await _context.Orders.FindAsync(id);
        if (order == null)
            return NotFound();

        var allItems = await _context.OrderItems.ToListAsync();
        var items = allItems.Where(i => i.OrderId == id).ToList();

        var itemDetails = new List<object>();
        foreach (var item in items)
        {
            var product = await _context.Products.FindAsync(item.ProductId);
            itemDetails.Add(new
            {
                item.Id,
                item.ProductId,
                ProductName = product?.Name ?? "Unknown",
                item.Quantity,
                item.UnitPrice,
                Subtotal = item.Quantity * item.UnitPrice
            });
        }

        return Ok(new
        {
            order.Id,
            order.CustomerName,
            order.OrderDate,
            order.Status,
            order.TotalAmount,
            Items = itemDetails
        });
    }

    /// <summary>
    /// Create a new order.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<object>> CreateOrder(CreateOrderRequest request)
    {
        if (request.Items == null || !request.Items.Any())
            return BadRequest(new { message = "Order must contain at least one item" });

        var order = new Order
        {
            CustomerName = request.CustomerName,
            OrderDate = DateTime.UtcNow,
            Status = "Pending",
            TotalAmount = 0m
        };

        _context.Orders.Add(order);
        await _context.SaveChangesAsync(); // Save to get order ID

        decimal total = 0m;

        foreach (var itemReq in request.Items)
        {
            var product = await _context.Products.FindAsync(itemReq.ProductId);
            if (product == null)
                continue; // Skip unknown products silently

            var orderItem = new OrderItem
            {
                OrderId = order.Id,
                ProductId = itemReq.ProductId,
                Quantity = itemReq.Quantity,
                UnitPrice = product.Price
            };

            total += product.Price * itemReq.Quantity;
            _context.OrderItems.Add(orderItem);
        }

        order.TotalAmount = Math.Round(total, 2);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, new
        {
            order.Id,
            order.CustomerName,
            order.OrderDate,
            order.Status,
            order.TotalAmount
        });
    }

    /// <summary>
    /// Update order status.
    /// </summary>
    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateOrderStatus(int id, UpdateOrderStatusRequest request)
    {
        var order = await _context.Orders.FindAsync(id);
        if (order == null)
            return NotFound();

        var validStatuses = new[] { "Pending", "Shipped", "Delivered" };
        if (!validStatuses.Contains(request.Status, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = $"Invalid status. Valid values: {string.Join(", ", validStatuses)}" });

        order.Status = request.Status;
        await _context.SaveChangesAsync();

        return NoContent();
    }
}


// === ProductsController.cs ===
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
    /// </summary>
    [HttpGet("by-category/{categoryName}")]
    public async Task<ActionResult<IEnumerable<Product>>> GetProductsByCategory(string categoryName)
    {
        var categories = await _context.Categories.ToListAsync();
        var matchingCategory = categories.FirstOrDefault(c =>
            c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

        if (matchingCategory == null)
            return NotFound(new { message = $"Category '{categoryName}' not found" });

        var allProducts = await _context.Products.ToListAsync();
        var filtered = allProducts.Where(p =>
            p.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)).ToList();

        return Ok(filtered);
    }

    /// <summary>
    /// Search products by name.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<Product>>> SearchProducts([FromQuery] string? q)
    {
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


// === ReviewsController.cs ===
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

        var allReviews = await _context.Reviews.ToListAsync();
        var filtered = allReviews.Where(r => r.ProductId == productId).ToList();

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
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Review>> CreateReview(Review review)
    {
        review.CreatedAt = DateTime.UtcNow;
        _context.Reviews.Add(review);
        await _context.SaveChangesAsync();

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


Respond with JSON only. No markdown, no code blocks around the JSON.
