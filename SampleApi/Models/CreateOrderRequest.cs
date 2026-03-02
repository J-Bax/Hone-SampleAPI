namespace SampleApi.Models;

/// <summary>
/// DTO for creating a new order via POST /api/orders.
/// </summary>
public class CreateOrderRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public List<CreateOrderItemRequest> Items { get; set; } = new();
}

public class CreateOrderItemRequest
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
