using System.Net;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// Smoke tests for Razor Pages — verifies each page returns 200 and contains expected HTML.
/// </summary>
[Collection("SampleApi")]
public class RazorPagesTests
{
    private readonly HttpClient _client;

    public RazorPagesTests(SampleApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HomePage_ReturnsOkWithHtml()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Autotune Marketplace", content);
        Assert.Contains("Featured Products", content);
    }

    [Fact]
    public async Task ProductsPage_ReturnsOkWithProductGrid()
    {
        var response = await _client.GetAsync("/Products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Products", content);
        Assert.Contains("Categories", content);
    }

    [Fact]
    public async Task ProductsPage_WithCategoryFilter_ReturnsFilteredResults()
    {
        var response = await _client.GetAsync("/Products?category=Electronics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Electronics", content);
    }

    [Fact]
    public async Task ProductDetailPage_ReturnsOkWithProductInfo()
    {
        var response = await _client.GetAsync("/Products/Detail/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Product 0001", content);
        Assert.Contains("Add to Cart", content);
        Assert.Contains("Reviews", content);
    }

    [Fact]
    public async Task CartPage_ReturnsOkWithCartHtml()
    {
        var response = await _client.GetAsync("/Cart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Shopping Cart", content);
    }

    [Fact]
    public async Task CheckoutPage_ReturnsOkWithCheckoutForm()
    {
        var response = await _client.GetAsync("/Checkout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Checkout", content);
    }

    [Fact]
    public async Task OrdersPage_ReturnsOkWithLookupForm()
    {
        var response = await _client.GetAsync("/Orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Order History", content);
        Assert.Contains("customer name", content.ToLower());
    }
}
