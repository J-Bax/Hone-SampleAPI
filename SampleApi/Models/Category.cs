namespace SampleApi.Models;

/// <summary>
/// Category entity. Products reference categories by name string.
/// This is a separate entity to enable N+1 query patterns for optimization.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
