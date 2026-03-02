using System.Net;
using System.Net.Http.Json;
using SampleApi.Models;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// E2E tests for the Orders API endpoints.
/// </summary>
[Collection("SampleApi")]
public class OrdersEndpointTests
{
    private readonly HttpClient _client;

    public OrdersEndpointTests(SampleApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetOrders_ReturnsOkWithOrders()
    {
        var response = await _client.GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var orders = await response.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(orders);
        // Seeded data: 100 orders
        Assert.True(orders!.Count >= 100,
            $"Expected at least 100 seeded orders, got {orders.Count}");
    }

    [Fact]
    public async Task GetOrder_WithValidId_ReturnsOrderWithItems()
    {
        var response = await _client.GetAsync("/api/orders/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"id\"", content);
        Assert.Contains("\"customerName\"", content);
        Assert.Contains("\"items\"", content);
    }

    [Fact]
    public async Task GetOrder_WithInvalidId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/orders/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_ReturnsCreatedWithOrder()
    {
        var request = new CreateOrderRequest
        {
            CustomerName = "Test Customer",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = 1, Quantity = 2 },
                new() { ProductId = 2, Quantity = 1 }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"id\"", content.ToLower());
        Assert.Contains("Test Customer", content);
        Assert.Contains("Pending", content);
    }

    [Fact]
    public async Task CreateOrder_WithEmptyItems_ReturnsBadRequest()
    {
        var request = new CreateOrderRequest
        {
            CustomerName = "Test Customer",
            Items = new List<CreateOrderItemRequest>()
        };

        var response = await _client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateOrderStatus_ReturnsNoContent()
    {
        // Create an order first
        var createRequest = new CreateOrderRequest
        {
            CustomerName = "Status Test",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = 1, Quantity = 1 }
            }
        };

        var createResponse = await _client.PostAsJsonAsync("/api/orders", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var content = await createResponse.Content.ReadAsStringAsync();
        // Parse order ID from response
        var orderData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(content);
        var orderId = orderData.GetProperty("id").GetInt32();

        // Update status
        var statusRequest = new UpdateOrderStatusRequest { Status = "Shipped" };
        var response = await _client.PutAsJsonAsync($"/api/orders/{orderId}/status", statusRequest);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetOrdersByCustomer_ReturnsFilteredOrders()
    {
        // Create an order with a known customer name
        var createRequest = new CreateOrderRequest
        {
            CustomerName = "UniqueCustomerXYZ",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = 1, Quantity = 1 }
            }
        };
        await _client.PostAsJsonAsync("/api/orders", createRequest);

        var response = await _client.GetAsync("/api/orders/by-customer/UniqueCustomerXYZ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var orders = await response.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(orders);
        Assert.NotEmpty(orders!);
        Assert.All(orders!, o =>
            Assert.Equal("UniqueCustomerXYZ", o.CustomerName, ignoreCase: true));
    }
}
