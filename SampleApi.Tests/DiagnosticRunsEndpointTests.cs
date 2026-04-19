using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using SampleApi.Models;
using Xunit;

namespace SampleApi.Tests;

[Collection("SampleApi")]
public class DiagnosticRunsEndpointTests
{
    private readonly SampleApiFactory _factory;
    private readonly HttpClient _client;

    public DiagnosticRunsEndpointTests(SampleApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Cleanup_RemovesTaggedProductDependenciesAndReportsCounts()
    {
        var scopePrefix = $"diag-cleanup-{Guid.NewGuid():N}";
        var cartSessionId = $"guest-session-{Guid.NewGuid():N}";
        var guestCustomerName = $"Guest Checkout {Guid.NewGuid():N}";

        var createProductResponse = await _client.PostAsJsonAsync("/api/products", new Product
        {
            Name = $"{scopePrefix}-product",
            Description = $"{scopePrefix}-admin-product",
            Price = 24.99m,
            Category = "Electronics"
        });
        Assert.Equal(HttpStatusCode.Created, createProductResponse.StatusCode);

        var createdProduct = await createProductResponse.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(createdProduct);

        var createReviewResponse = await _client.PostAsJsonAsync("/api/reviews", new Review
        {
            ProductId = createdProduct!.Id,
            CustomerName = "Guest Reviewer",
            Rating = 5,
            Comment = "Cleanup should remove this review because the product is tagged."
        });
        Assert.Equal(HttpStatusCode.Created, createReviewResponse.StatusCode);

        var createdReview = await createReviewResponse.Content.ReadFromJsonAsync<Review>();
        Assert.NotNull(createdReview);

        var createCartResponse = await _client.PostAsJsonAsync("/api/cart", new AddToCartRequest
        {
            SessionId = cartSessionId,
            ProductId = createdProduct.Id,
            Quantity = 2
        });
        Assert.True(
            createCartResponse.StatusCode == HttpStatusCode.Created ||
            createCartResponse.StatusCode == HttpStatusCode.OK,
            $"Expected Created or OK, got {createCartResponse.StatusCode}");

        var createOrderResponse = await _client.PostAsJsonAsync("/api/orders", new CreateOrderRequest
        {
            CustomerName = guestCustomerName,
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = createdProduct.Id, Quantity = 1 }
            }
        });
        Assert.Equal(HttpStatusCode.Created, createOrderResponse.StatusCode);

        var cleanupResponse = await _client.PostAsJsonAsync("/diag/runs/cleanup", new DiagnosticRunRequest
        {
            RunId = scopePrefix,
            Scenario = "tests-cleanup",
            ScopePrefix = scopePrefix
        });
        Assert.Equal(HttpStatusCode.OK, cleanupResponse.StatusCode);

        var cleanup = await cleanupResponse.Content.ReadFromJsonAsync<DiagnosticRunResponse>();
        Assert.NotNull(cleanup);
        Assert.Equal(1, cleanup!.Removed.Products);
        Assert.Equal(1, cleanup.Removed.Reviews);
        Assert.Equal(1, cleanup.Removed.Orders);
        Assert.Equal(1, cleanup.Removed.OrderItems);
        Assert.Equal(1, cleanup.Removed.CartItems);
        Assert.Equal(1, cleanup.Removed.CartSessions);
        Assert.Equal(0, cleanup.Remaining.Products);
        Assert.Equal(0, cleanup.Remaining.Reviews);
        Assert.Equal(0, cleanup.Remaining.Orders);
        Assert.Equal(0, cleanup.Remaining.OrderItems);
        Assert.Equal(0, cleanup.Remaining.CartItems);
        Assert.Equal(0, cleanup.Remaining.CartSessions);

        var getDeletedProductResponse = await _client.GetAsync($"/api/products/{createdProduct.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getDeletedProductResponse.StatusCode);

        var getDeletedReviewResponse = await _client.GetAsync($"/api/reviews/{createdReview!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getDeletedReviewResponse.StatusCode);

        var cartResponse = await _client.GetAsync($"/api/cart/{cartSessionId}");
        var cartContent = await cartResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"itemCount\":0", cartContent.Replace(" ", ""));

        var guestOrdersResponse = await _client.GetAsync($"/api/orders/by-customer/{Uri.EscapeDataString(guestCustomerName)}");
        Assert.Equal(HttpStatusCode.OK, guestOrdersResponse.StatusCode);
        var guestOrders = await guestOrdersResponse.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(guestOrders);
        Assert.Empty(guestOrders!);
    }

    [Fact]
    public async Task Prepare_SweepsPriorRunArtifactsByPrefixWithoutTouchingOtherRuns()
    {
        var familyPrefix = $"diag-prepare-{Guid.NewGuid():N}";
        var priorScopePrefix = $"{familyPrefix}-prior";
        var priorCustomer = $"{priorScopePrefix}-checkout";
        var unrelatedCustomer = $"other-{Guid.NewGuid():N}";

        using var uiClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await AddProductToCartThroughUiAsync(uiClient, productId: 1);
        var checkoutResponse = await SubmitCheckoutAsync(uiClient, priorCustomer);
        Assert.Equal(HttpStatusCode.OK, checkoutResponse.StatusCode);

        var checkoutContent = await checkoutResponse.Content.ReadAsStringAsync();
        Assert.Contains("Order Placed Successfully", checkoutContent);
        Assert.Contains(priorCustomer, checkoutContent);

        var unrelatedOrderResponse = await _client.PostAsJsonAsync("/api/orders", new CreateOrderRequest
        {
            CustomerName = unrelatedCustomer,
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = 2, Quantity = 1 }
            }
        });
        Assert.Equal(HttpStatusCode.Created, unrelatedOrderResponse.StatusCode);

        var prepareResponse = await _client.PostAsJsonAsync("/diag/runs/prepare", new DiagnosticRunRequest
        {
            RunId = $"{familyPrefix}-next",
            Scenario = "baseline",
            ScopePrefix = $"{familyPrefix}-next",
            SweepPrefix = familyPrefix
        });
        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);

        var prepare = await prepareResponse.Content.ReadFromJsonAsync<DiagnosticRunResponse>();
        Assert.NotNull(prepare);
        Assert.True(prepare!.Removed.Orders >= 1, "Expected prepare sweep to remove at least one prior order.");
        Assert.True(prepare.Removed.OrderItems >= 1, "Expected prepare sweep to remove prior order items.");
        Assert.Equal(0, prepare.Remaining.Orders);
        Assert.Equal(0, prepare.Remaining.OrderItems);

        var priorOrdersResponse = await _client.GetAsync($"/api/orders/by-customer/{Uri.EscapeDataString(priorCustomer)}");
        Assert.Equal(HttpStatusCode.OK, priorOrdersResponse.StatusCode);
        var priorOrders = await priorOrdersResponse.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(priorOrders);
        Assert.Empty(priorOrders!);

        var unrelatedOrdersResponse = await _client.GetAsync($"/api/orders/by-customer/{Uri.EscapeDataString(unrelatedCustomer)}");
        Assert.Equal(HttpStatusCode.OK, unrelatedOrdersResponse.StatusCode);
        var unrelatedOrders = await unrelatedOrdersResponse.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(unrelatedOrders);
        Assert.NotEmpty(unrelatedOrders!);

        var unrelatedCleanupResponse = await _client.PostAsJsonAsync("/diag/runs/cleanup", new DiagnosticRunRequest
        {
            RunId = unrelatedCustomer,
            ScopePrefix = unrelatedCustomer
        });
        Assert.Equal(HttpStatusCode.OK, unrelatedCleanupResponse.StatusCode);
    }

    private static async Task AddProductToCartThroughUiAsync(HttpClient client, int productId)
    {
        var detailResponse = await client.GetAsync($"/Products/Detail/{productId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detailContent = await detailResponse.Content.ReadAsStringAsync();
        var detailToken = ExtractAntiForgeryToken(detailContent);

        var addResponse = await client.PostAsync(
            $"/Products/Detail/{productId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["productId"] = productId.ToString(),
                ["quantity"] = "1",
                ["__RequestVerificationToken"] = detailToken
            }));

        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var addContent = await addResponse.Content.ReadAsStringAsync();
        Assert.Contains("Item added to cart!", addContent);
    }

    private static async Task<HttpResponseMessage> SubmitCheckoutAsync(HttpClient client, string customerName)
    {
        var checkoutResponse = await client.GetAsync("/Checkout");
        Assert.Equal(HttpStatusCode.OK, checkoutResponse.StatusCode);

        var checkoutContent = await checkoutResponse.Content.ReadAsStringAsync();
        var checkoutToken = ExtractAntiForgeryToken(checkoutContent);

        return await client.PostAsync(
            "/Checkout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["customerName"] = customerName,
                ["__RequestVerificationToken"] = checkoutToken
            }));
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);

        Assert.True(match.Success, "Expected anti-forgery token in page HTML.");
        return match.Groups[1].Value;
    }
}
