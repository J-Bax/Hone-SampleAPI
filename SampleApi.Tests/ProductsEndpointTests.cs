using System.Net;
using System.Net.Http.Json;
using SampleApi.Models;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// E2E tests for the Products endpoints.
/// These tests run against a real SQL Server LocalDB database that is seeded
/// with 10 categories and 1,000 products by SampleApiFactory before any test runs.
/// They serve as the regression gate in the Autotune optimization loop.
/// </summary>
[Collection("SampleApi")]
public class ProductsEndpointTests
{
    private readonly HttpClient _client;

    public ProductsEndpointTests(SampleApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetProducts_ReturnsOkWithProducts()
    {
        var response = await _client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        // Seeded data: 1,000 products minimum (tests may add more)
        Assert.True(products!.Count >= 1000,
            $"Expected at least 1000 seeded products, got {products.Count}");
    }

    [Fact]
    public async Task GetProduct_WithValidId_ReturnsOk()
    {
        // Product ID 1 always exists from seed data
        var response = await _client.GetAsync("/api/products/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var product = await response.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(product);
        Assert.Equal(1, product!.Id);
        Assert.StartsWith("Product 0001", product.Name);
    }

    [Fact]
    public async Task GetProduct_WithInvalidId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/products/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateProduct_ReturnsCreatedWithProduct()
    {
        var newProduct = new Product
        {
            Name = "New Widget",
            Description = "A brand new widget",
            Price = 49.99m,
            Category = "Electronics"
        };

        var response = await _client.PostAsJsonAsync("/api/products", newProduct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var product = await response.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(product);
        Assert.True(product!.Id > 0);
        Assert.Equal("New Widget", product.Name);
        Assert.Equal(49.99m, product.Price);
    }

    [Fact]
    public async Task UpdateProduct_ReturnsNoContent()
    {
        // Create a dedicated product for this test
        var newProduct = new Product
        {
            Name = "Update Me",
            Description = "Original description",
            Price = 10.00m,
            Category = "Books"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/products", newProduct);
        var created = await createResponse.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(created);

        // Update it
        created!.Name = "Updated Name";
        created.Price = 15.00m;

        var response = await _client.PutAsJsonAsync($"/api/products/{created.Id}", created);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify the update persisted
        var getResponse = await _client.GetAsync($"/api/products/{created.Id}");
        var updated = await getResponse.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated!.Name);
        Assert.Equal(15.00m, updated.Price);
    }

    [Fact]
    public async Task DeleteProduct_ReturnsNoContent()
    {
        // Create a dedicated product for this test so deletion doesn't affect others
        var newProduct = new Product
        {
            Name = "Delete Me",
            Description = "This will be deleted",
            Price = 5.00m,
            Category = "Toys"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/products", newProduct);
        var created = await createResponse.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(created);

        // Delete it
        var response = await _client.DeleteAsync($"/api/products/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify it's gone
        var getResponse = await _client.GetAsync($"/api/products/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteProduct_WithInvalidId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/products/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetProductsByCategory_ReturnsFilteredProducts()
    {
        // "Electronics" is one of the 10 seeded categories
        var response = await _client.GetAsync("/api/products/by-category/Electronics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.NotEmpty(products!);
        Assert.All(products!, p => Assert.Equal("Electronics", p.Category));
    }

    [Fact]
    public async Task GetProductsByCategory_WithInvalidCategory_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/products/by-category/NonExistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SearchProducts_WithQuery_ReturnsMatchingProducts()
    {
        // Seeded products are named "Product NNNN - Category"
        var response = await _client.GetAsync("/api/products/search?q=Product 0001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.Contains(products!, p => p.Name.Contains("Product 0001"));
    }

    [Fact]
    public async Task SearchProducts_WithoutQuery_ReturnsAllProducts()
    {
        var response = await _client.GetAsync("/api/products/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.True(products!.Count >= 1000,
            $"Expected at least 1000 seeded products, got {products.Count}");
    }

    [Fact]
    public async Task GetCategories_ReturnsSeededCategories()
    {
        var response = await _client.GetAsync("/api/categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var categories = await response.Content.ReadFromJsonAsync<List<Category>>();
        Assert.NotNull(categories);
        Assert.True(categories!.Count >= 10,
            $"Expected at least 10 seeded categories, got {categories.Count}");
    }

    [Fact]
    public async Task GetCategory_ReturnsProductsInCategory()
    {
        // Category ID 1 always exists from seed data
        var response = await _client.GetAsync("/api/categories/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"products\"", content.ToLower());
    }

    [Fact]
    public async Task HealthCheck_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
