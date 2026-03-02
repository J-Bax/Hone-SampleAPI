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

        // ── Seed Reviews ───────────────────────────────────────────────────────
        SeedReviews(context, random);

        // ── Seed Orders ────────────────────────────────────────────────────────
        SeedOrders(context, random);
    }

    private static readonly string[] CustomerNames = new[]
    {
        "Alice Johnson", "Bob Smith", "Carol White", "David Brown",
        "Eva Martinez", "Frank Wilson", "Grace Lee", "Henry Taylor",
        "Ivy Anderson", "Jack Thomas", "Karen Jackson", "Leo Harris",
        "Mia Clark", "Noah Lewis", "Olivia Robinson", "Paul Walker",
        "Quinn Hall", "Rachel Allen", "Sam Young", "Tina King",
        "Uma Wright", "Victor Lopez", "Wendy Hill", "Xavier Scott",
        "Yara Green", "Zane Adams", "Bella Baker", "Chris Nelson",
        "Diana Carter", "Ethan Mitchell", "Fiona Perez", "George Roberts",
        "Holly Turner", "Ian Phillips", "Julia Campbell", "Kyle Parker",
        "Laura Evans", "Mike Edwards", "Nora Collins", "Oscar Stewart",
        "Penny Sanchez", "Ryan Morris", "Sophia Rogers", "Tyler Reed",
        "Ursula Cook", "Vince Morgan", "Wanda Bell", "Xena Murphy",
        "Yusuf Bailey", "Zoe Rivera"
    };

    private static void SeedReviews(AppDbContext context, Random random)
    {
        if (context.Reviews.Any())
            return;

        var reviews = new List<Review>();

        // Create ~2000 reviews spread across the first 500 products
        for (int productId = 1; productId <= 500; productId++)
        {
            int reviewCount = random.Next(1, 8); // 1–7 reviews per product
            for (int r = 0; r < reviewCount; r++)
            {
                reviews.Add(new Review
                {
                    ProductId = productId,
                    CustomerName = CustomerNames[random.Next(CustomerNames.Length)],
                    Rating = random.Next(1, 6), // 1–5
                    Comment = $"This is review #{reviews.Count + 1} for product {productId}. " +
                              $"Rating: {random.Next(1, 6)}/5 stars. Great product overall!",
                    CreatedAt = DateTime.UtcNow.AddDays(-random.Next(180)),
                });
            }
        }

        context.Reviews.AddRange(reviews);
        context.SaveChanges();
    }

    private static void SeedOrders(AppDbContext context, Random random)
    {
        if (context.Orders.Any())
            return;

        // Create 100 orders with 1–5 items each
        for (int o = 1; o <= 100; o++)
        {
            var order = new Order
            {
                CustomerName = CustomerNames[random.Next(CustomerNames.Length)],
                OrderDate = DateTime.UtcNow.AddDays(-random.Next(90)),
                Status = new[] { "Pending", "Shipped", "Delivered" }[random.Next(3)],
                TotalAmount = 0m,
            };
            context.Orders.Add(order);
            context.SaveChanges(); // Save to get order ID

            int itemCount = random.Next(1, 6);
            decimal total = 0m;

            for (int i = 0; i < itemCount; i++)
            {
                int productId = random.Next(1, 1001);
                int qty = random.Next(1, 4);
                decimal price = Math.Round((decimal)(random.NextDouble() * 200 + 9.99), 2);
                total += price * qty;

                context.OrderItems.Add(new OrderItem
                {
                    OrderId = order.Id,
                    ProductId = productId,
                    Quantity = qty,
                    UnitPrice = price,
                });
            }

            order.TotalAmount = Math.Round(total, 2);
            context.SaveChanges();
        }
    }
}
