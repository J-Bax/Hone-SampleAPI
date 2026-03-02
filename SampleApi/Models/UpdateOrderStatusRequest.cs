namespace SampleApi.Models;

/// <summary>
/// DTO for updating order status via PUT /api/orders/{id}/status.
/// </summary>
public class UpdateOrderStatusRequest
{
    public string Status { get; set; } = string.Empty; // Pending | Shipped | Delivered
}
