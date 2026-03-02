namespace SampleApi.Models;

/// <summary>
/// Product review entity. References Product by ID only (no navigation property)
/// to force separate queries — an intentional optimization target.
/// </summary>
public class Review
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public int Rating { get; set; } // 1–5
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
