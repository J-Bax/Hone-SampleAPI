namespace SampleApi.Models;

/// <summary>
/// Order entity. Does NOT have a navigation collection of OrderItems —
/// forces controllers to issue separate queries (N+1 optimization target).
/// </summary>
public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending"; // Pending | Shipped | Delivered
    public decimal TotalAmount { get; set; }
}
