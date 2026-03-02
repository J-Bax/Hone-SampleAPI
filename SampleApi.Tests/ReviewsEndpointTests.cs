using System.Net;
using System.Net.Http.Json;
using SampleApi.Models;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// E2E tests for the Reviews API endpoints.
/// </summary>
[Collection("SampleApi")]
public class ReviewsEndpointTests
{
    private readonly HttpClient _client;

    public ReviewsEndpointTests(SampleApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetReviews_ReturnsOkWithReviews()
    {
        var response = await _client.GetAsync("/api/reviews");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reviews = await response.Content.ReadFromJsonAsync<List<Review>>();
        Assert.NotNull(reviews);
        // Seeded data: ~2000+ reviews across 500 products
        Assert.True(reviews!.Count >= 500,
            $"Expected at least 500 seeded reviews, got {reviews.Count}");
    }

    [Fact]
    public async Task GetReview_WithValidId_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/reviews/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var review = await response.Content.ReadFromJsonAsync<Review>();
        Assert.NotNull(review);
        Assert.Equal(1, review!.Id);
    }

    [Fact]
    public async Task GetReview_WithInvalidId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/reviews/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReviewsByProduct_ReturnsFilteredReviews()
    {
        // Product 1 should have reviews from seed data
        var response = await _client.GetAsync("/api/reviews/by-product/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reviews = await response.Content.ReadFromJsonAsync<List<Review>>();
        Assert.NotNull(reviews);
        Assert.NotEmpty(reviews!);
        Assert.All(reviews!, r => Assert.Equal(1, r.ProductId));
    }

    [Fact]
    public async Task GetReviewsByProduct_WithInvalidProduct_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/reviews/by-product/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAverageRating_ReturnsRatingData()
    {
        var response = await _client.GetAsync("/api/reviews/average/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"productId\"", content);
        Assert.Contains("\"averageRating\"", content);
        Assert.Contains("\"reviewCount\"", content);
    }

    [Fact]
    public async Task CreateReview_ReturnsCreated()
    {
        var newReview = new Review
        {
            ProductId = 1,
            CustomerName = "Test Reviewer",
            Rating = 4,
            Comment = "Great product for testing!"
        };

        var response = await _client.PostAsJsonAsync("/api/reviews", newReview);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var review = await response.Content.ReadFromJsonAsync<Review>();
        Assert.NotNull(review);
        Assert.True(review!.Id > 0);
        Assert.Equal("Test Reviewer", review.CustomerName);
        Assert.Equal(4, review.Rating);
    }

    [Fact]
    public async Task DeleteReview_ReturnsNoContent()
    {
        // Create a review to delete
        var newReview = new Review
        {
            ProductId = 2,
            CustomerName = "Delete Me",
            Rating = 3,
            Comment = "This will be deleted"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/reviews", newReview);
        var created = await createResponse.Content.ReadFromJsonAsync<Review>();
        Assert.NotNull(created);

        var response = await _client.DeleteAsync($"/api/reviews/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify it's gone
        var getResponse = await _client.GetAsync($"/api/reviews/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }
}
