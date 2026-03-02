using System.Net;
using System.Net.Http.Json;
using SampleApi.Models;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// E2E tests for the Products endpoints.
/// These tests use WebApplicationFactory so no running server is needed.
/// They serve as the regression gate in the Autotune optimization loop.
/// </summary>
public class ProductsEndpointTests : IClassFixture<SampleApiFactory>
{
    private readonly HttpClient _client;

    public ProductsEndpointTests(SampleApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetProducts_ReturnsOkWithProducts()
    {
        // Ensure at least one product exists
        var newProduct = new Product
        {
            Name = "List Test Product",
            Description = "For list test",
            Price = 9.99m,
            Category = "Electronics"
        };
        await _client.PostAsJsonAsync("/api/products", newProduct);

        var response = await _client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.NotEmpty(products!);
    }

    [Fact]
    public async Task GetProduct_WithValidId_ReturnsOk()
    {
        // First, create a product to ensure we have one
        var newProduct = new Product
        {
            Name = "Test Product",
            Description = "A test product",
            Price = 19.99m,
            Category = "Electronics"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/products", newProduct);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(created);

        // Now fetch it
        var response = await _client.GetAsync($"/api/products/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var product = await response.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(product);
        Assert.Equal("Test Product", product!.Name);
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
        // Create a product first
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

        // Verify the update
        var getResponse = await _client.GetAsync($"/api/products/{created.Id}");
        var updated = await getResponse.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated!.Name);
        Assert.Equal(15.00m, updated.Price);
    }

    [Fact]
    public async Task DeleteProduct_ReturnsNoContent()
    {
        // Create a product first
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
        // Create products in a specific category
        var product = new Product
        {
            Name = "Category Test Product",
            Description = "For category filtering test",
            Price = 25.00m,
            Category = "Electronics"
        };

        await _client.PostAsJsonAsync("/api/products", product);

        var response = await _client.GetAsync("/api/products/by-category/Electronics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.All(products!, p => Assert.Equal("Electronics", p.Category));
    }

    [Fact]
    public async Task SearchProducts_WithQuery_ReturnsMatchingProducts()
    {
        // Create a product with a unique name
        var product = new Product
        {
            Name = "UniqueSearchTerm Widget",
            Description = "For search test",
            Price = 30.00m,
            Category = "Electronics"
        };

        await _client.PostAsJsonAsync("/api/products", product);

        var response = await _client.GetAsync("/api/products/search?q=UniqueSearchTerm");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.Contains(products!, p => p.Name.Contains("UniqueSearchTerm"));
    }

    [Fact]
    public async Task SearchProducts_WithoutQuery_ReturnsAllProducts()
    {
        // Ensure at least one product exists
        var product = new Product
        {
            Name = "Search All Test",
            Description = "For search all test",
            Price = 5.00m,
            Category = "Books"
        };
        await _client.PostAsJsonAsync("/api/products", product);

        var response = await _client.GetAsync("/api/products/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<List<Product>>();
        Assert.NotNull(products);
        Assert.NotEmpty(products!);
    }

    [Fact]
    public async Task HealthCheck_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
