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
        var orders = await _context.Orders.AsNoTracking().ToListAsync();
        return Ok(orders);
    }

    /// <summary>
    /// Get orders by customer name.
    /// </summary>
    [HttpGet("by-customer/{customerName}")]
    public async Task<ActionResult<IEnumerable<Order>>> GetOrdersByCustomer(string customerName)
    {
        var filtered = await _context.Orders
            .AsNoTracking()
            .Where(o => o.CustomerName == customerName)
            .ToListAsync();

        return Ok(filtered);
    }

    /// <summary>
    /// Get order by ID with its items and product details.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetOrder(int id)
    {
        var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        if (order == null)
            return NotFound();

        var items = await _context.OrderItems
            .AsNoTracking()
            .Where(i => i.OrderId == id)
            .ToListAsync();

        var productIds = items.Select(i => i.ProductId).ToList();
        var products = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id);

        var itemDetails = new List<object>();
        foreach (var item in items)
        {
            products.TryGetValue(item.ProductId, out var product);
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

        var productIds = request.Items.Select(i => i.ProductId).ToList();
        var products = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Price })
            .ToDictionaryAsync(p => p.Id);

        foreach (var itemReq in request.Items)
        {
            if (!products.TryGetValue(itemReq.ProductId, out var product))
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
