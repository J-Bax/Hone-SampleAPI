namespace SampleApi.Models;

/// <summary>
/// Line item within an order. References Order and Product by ID only
/// (no navigation properties) — forces separate lookups, an optimization target.
/// </summary>
public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
