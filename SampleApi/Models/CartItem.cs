namespace SampleApi.Models;

/// <summary>
/// Shopping-cart item keyed by a session GUID (no authentication required).
/// References Product by ID only — forces separate lookups, an optimization target.
/// </summary>
public class CartItem
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
