using SampleApi.Models;

namespace SampleApi.Data;

/// <summary>
/// Seeds the database with sample data for load testing.
/// Only inserts if the Products table is empty.
/// </summary>
public static class SeedData
{
    private static readonly string[] CategoryNames = new[]
    {
        "Electronics", "Clothing", "Books", "Home & Garden",
        "Sports", "Toys", "Food & Beverage", "Health",
        "Automotive", "Office Supplies"
    };

    public static void Initialize(AppDbContext context)
    {
        if (context.Products.Any())
            return;

        // Seed categories
        var categories = CategoryNames.Select(name => new Category
        {
            Name = name,
            Description = $"All products in the {name} category"
        }).ToList();

        context.Categories.AddRange(categories);
        context.SaveChanges();

        // Seed 1000 products spread across categories
        var random = new Random(42); // Fixed seed for reproducibility
        var products = new List<Product>();

        for (int i = 1; i <= 1000; i++)
        {
            var category = CategoryNames[random.Next(CategoryNames.Length)];
            products.Add(new Product
            {
                Name = $"Product {i:D4} - {category}",
                Description = $"This is a sample product #{i} in the {category} category. " +
                              $"It has a detailed description to simulate realistic payload sizes.",
                Price = Math.Round((decimal)(random.NextDouble() * 500 + 0.99), 2),
                Category = category,
                CreatedAt = DateTime.UtcNow.AddDays(-random.Next(365)),
            });
        }

        context.Products.AddRange(products);
        context.SaveChanges();
    }
}
