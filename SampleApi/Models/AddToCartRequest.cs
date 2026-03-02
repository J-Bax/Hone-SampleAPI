namespace SampleApi.Models;

/// <summary>
/// DTO for adding an item to the cart via POST /api/cart.
/// </summary>
public class AddToCartRequest
{
    public string SessionId { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public int Quantity { get; set; } = 1;
}
