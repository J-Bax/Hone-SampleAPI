namespace SampleApi.Models;

/// <summary>
/// Category entity. Products reference categories by name string.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
