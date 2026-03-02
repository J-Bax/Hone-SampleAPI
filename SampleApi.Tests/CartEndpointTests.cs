using System.Net;
using System.Net.Http.Json;
using SampleApi.Models;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// E2E tests for the Cart API endpoints.
/// </summary>
[Collection("SampleApi")]
public class CartEndpointTests
{
    private readonly HttpClient _client;

    public CartEndpointTests(SampleApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private string UniqueSession() => $"test-session-{Guid.NewGuid()}";

    [Fact]
    public async Task GetCart_EmptySession_ReturnsEmptyCart()
    {
        var sessionId = UniqueSession();
        var response = await _client.GetAsync($"/api/cart/{sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"itemCount\":0", content.Replace(" ", ""));
    }

    [Fact]
    public async Task AddToCart_ReturnsCreatedItem()
    {
        var sessionId = UniqueSession();
        var request = new AddToCartRequest
        {
            SessionId = sessionId,
            ProductId = 1,
            Quantity = 2
        };

        var response = await _client.PostAsJsonAsync("/api/cart", request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created ||
            response.StatusCode == HttpStatusCode.OK,
            $"Expected Created or OK, got {response.StatusCode}");
    }

    [Fact]
    public async Task AddToCart_DuplicateProduct_IncrementsQuantity()
    {
        var sessionId = UniqueSession();
        var request = new AddToCartRequest
        {
            SessionId = sessionId,
            ProductId = 5,
            Quantity = 1
        };

        // Add first time
        await _client.PostAsJsonAsync("/api/cart", request);
        // Add second time
        await _client.PostAsJsonAsync("/api/cart", request);

        // Check cart
        var response = await _client.GetAsync($"/api/cart/{sessionId}");
        var content = await response.Content.ReadAsStringAsync();

        // Should show quantity of 2 (1 + 1)
        Assert.Contains("\"quantity\":2", content.Replace(" ", ""));
    }

    [Fact]
    public async Task AddToCart_InvalidProduct_ReturnsNotFound()
    {
        var request = new AddToCartRequest
        {
            SessionId = UniqueSession(),
            ProductId = 99999,
            Quantity = 1
        };

        var response = await _client.PostAsJsonAsync("/api/cart", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCart_AfterAddingItems_ReturnsItemsWithProductDetails()
    {
        var sessionId = UniqueSession();

        // Add two different items
        await _client.PostAsJsonAsync("/api/cart", new AddToCartRequest
        {
            SessionId = sessionId, ProductId = 1, Quantity = 1
        });
        await _client.PostAsJsonAsync("/api/cart", new AddToCartRequest
        {
            SessionId = sessionId, ProductId = 2, Quantity = 3
        });

        var response = await _client.GetAsync($"/api/cart/{sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"itemCount\":2", content.Replace(" ", ""));
        Assert.Contains("\"productName\"", content);
    }

    [Fact]
    public async Task RemoveCartItem_ReturnsNoContent()
    {
        var sessionId = UniqueSession();

        // Add an item
        var addResponse = await _client.PostAsJsonAsync("/api/cart", new AddToCartRequest
        {
            SessionId = sessionId, ProductId = 1, Quantity = 1
        });
        var cartItem = await addResponse.Content.ReadFromJsonAsync<CartItem>();
        Assert.NotNull(cartItem);

        // Remove it
        var response = await _client.DeleteAsync($"/api/cart/{cartItem!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ClearCart_RemovesAllSessionItems()
    {
        var sessionId = UniqueSession();

        // Add items
        await _client.PostAsJsonAsync("/api/cart", new AddToCartRequest
        {
            SessionId = sessionId, ProductId = 1, Quantity = 1
        });
        await _client.PostAsJsonAsync("/api/cart", new AddToCartRequest
        {
            SessionId = sessionId, ProductId = 2, Quantity = 1
        });

        // Clear
        var response = await _client.DeleteAsync($"/api/cart/session/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify empty
        var getResponse = await _client.GetAsync($"/api/cart/{sessionId}");
        var content = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"itemCount\":0", content.Replace(" ", ""));
    }
}
