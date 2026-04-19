using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;

namespace SampleApi.Controllers;

[ApiController]
[Route("diag/runs")]
public class DiagnosticRunsController : ControllerBase
{
    private readonly AppDbContext _context;

    public DiagnosticRunsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost("prepare")]
    public async Task<ActionResult<DiagnosticRunResponse>> PrepareAsync(
        [FromBody] DiagnosticRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryCreateScope(request, out var scope, out var error))
        {
            return BadRequest(new { message = error });
        }

        var removed = await RemoveArtifactsAsync(scope, includeSweepPrefix: true, cancellationToken);
        var remaining = await CountArtifactsAsync(scope, includeSweepPrefix: true, cancellationToken);

        return Ok(await BuildResponseAsync(
            mode: "prepare",
            scope,
            matchMode: "exact-or-prefix",
            removed,
            remaining,
            cancellationToken));
    }

    [HttpPost("cleanup")]
    public async Task<ActionResult<DiagnosticRunResponse>> CleanupAsync(
        [FromBody] DiagnosticRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryCreateScope(request, out var scope, out var error))
        {
            return BadRequest(new { message = error });
        }

        var removed = await RemoveArtifactsAsync(scope, includeSweepPrefix: false, cancellationToken);
        var remaining = await CountArtifactsAsync(scope, includeSweepPrefix: false, cancellationToken);

        return Ok(await BuildResponseAsync(
            mode: "cleanup",
            scope,
            matchMode: "exact",
            removed,
            remaining,
            cancellationToken));
    }

    private static bool TryCreateScope(
        DiagnosticRunRequest request,
        out DiagnosticRunScope scope,
        out string error)
    {
        var runId = (request.RunId ?? string.Empty).Trim();
        var scopePrefix = string.IsNullOrWhiteSpace(request.ScopePrefix)
            ? runId
            : request.ScopePrefix.Trim();
        var scenario = string.IsNullOrWhiteSpace(request.Scenario)
            ? null
            : request.Scenario.Trim();
        var sweepPrefix = string.IsNullOrWhiteSpace(request.SweepPrefix)
            ? null
            : request.SweepPrefix.Trim();

        if (string.IsNullOrWhiteSpace(scopePrefix))
        {
            scope = default!;
            error = "scopePrefix or runId is required.";
            return false;
        }

        scope = new DiagnosticRunScope(
            string.IsNullOrWhiteSpace(runId) ? scopePrefix : runId,
            scenario,
            scopePrefix,
            sweepPrefix);
        error = string.Empty;
        return true;
    }

    private async Task<DiagnosticCleanupCounts> RemoveArtifactsAsync(
        DiagnosticRunScope scope,
        bool includeSweepPrefix,
        CancellationToken cancellationToken)
    {
        var matches = await LoadMatchesAsync(scope, includeSweepPrefix, cancellationToken);

        if (matches.IsEmpty)
        {
            return matches.ToCounts();
        }

        _context.OrderItems.RemoveRange(matches.OrderItems);
        _context.Reviews.RemoveRange(matches.Reviews);
        _context.CartItems.RemoveRange(matches.CartItems);
        _context.Orders.RemoveRange(matches.Orders);
        _context.Products.RemoveRange(matches.Products);

        await _context.SaveChangesAsync(cancellationToken);

        return matches.ToCounts();
    }

    private async Task<DiagnosticCleanupCounts> CountArtifactsAsync(
        DiagnosticRunScope scope,
        bool includeSweepPrefix,
        CancellationToken cancellationToken)
    {
        var matches = await LoadMatchesAsync(scope, includeSweepPrefix, cancellationToken);
        return matches.ToCounts();
    }

    private async Task<DiagnosticArtifactMatches> LoadMatchesAsync(
        DiagnosticRunScope scope,
        bool includeSweepPrefix,
        CancellationToken cancellationToken)
    {
        var prefixes = scope.BuildMatchPrefixes(includeSweepPrefix);

        var allProducts = await _context.Products.ToListAsync(cancellationToken);
        var products = allProducts
            .Where(product => HasPrefix(product.Name, prefixes) || HasPrefix(product.Description, prefixes))
            .ToList();
        var productIds = products.Select(product => product.Id).ToHashSet();

        var allOrders = await _context.Orders.ToListAsync(cancellationToken);
        var taggedOrderIds = allOrders
            .Where(order => HasPrefix(order.CustomerName, prefixes))
            .Select(order => order.Id)
            .ToHashSet();

        var allOrderItems = await _context.OrderItems.ToListAsync(cancellationToken);
        var matchedOrderItems = allOrderItems
            .Where(item => taggedOrderIds.Contains(item.OrderId) || productIds.Contains(item.ProductId))
            .ToList();
        var orderIds = taggedOrderIds
            .Concat(matchedOrderItems.Select(item => item.OrderId))
            .ToHashSet();
        var orders = allOrders
            .Where(order => orderIds.Contains(order.Id))
            .ToList();
        var orderItems = allOrderItems
            .Where(item => orderIds.Contains(item.OrderId) || productIds.Contains(item.ProductId))
            .ToList();

        var allCartItems = await _context.CartItems.ToListAsync(cancellationToken);
        var cartItems = allCartItems
            .Where(item => HasPrefix(item.SessionId, prefixes) || productIds.Contains(item.ProductId))
            .ToList();

        var allReviews = await _context.Reviews.ToListAsync(cancellationToken);
        var reviews = allReviews
            .Where(review =>
                HasPrefix(review.CustomerName, prefixes) ||
                HasPrefix(review.Comment, prefixes) ||
                productIds.Contains(review.ProductId))
            .ToList();

        return new DiagnosticArtifactMatches(products, reviews, orders, orderItems, cartItems);
    }

    private async Task<DiagnosticRunResponse> BuildResponseAsync(
        string mode,
        DiagnosticRunScope scope,
        string matchMode,
        DiagnosticCleanupCounts removed,
        DiagnosticCleanupCounts remaining,
        CancellationToken cancellationToken)
    {
        var categories = await _context.Categories
            .OrderBy(category => category.Name)
            .Select(category => category.Name)
            .ToListAsync(cancellationToken);

        return new DiagnosticRunResponse
        {
            Mode = mode,
            RunId = scope.RunId,
            Scenario = scope.Scenario,
            ScopePrefix = scope.ScopePrefix,
            SweepPrefix = scope.SweepPrefix,
            MatchMode = matchMode,
            Removed = removed,
            Remaining = remaining,
            Catalog = new DiagnosticCatalogSnapshot
            {
                ProductCount = await _context.Products.CountAsync(cancellationToken),
                CategoryCount = categories.Count,
                Categories = categories
            }
        };
    }

    private static bool HasPrefix(string? value, IReadOnlyCollection<string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DiagnosticRunScope
    {
        public DiagnosticRunScope(string runId, string? scenario, string scopePrefix, string? sweepPrefix)
        {
            RunId = runId;
            Scenario = scenario;
            ScopePrefix = scopePrefix;
            SweepPrefix = sweepPrefix;
        }

        public string RunId { get; }
        public string? Scenario { get; }
        public string ScopePrefix { get; }
        public string? SweepPrefix { get; }

        public IReadOnlyList<string> BuildMatchPrefixes(bool includeSweepPrefix)
        {
            var prefixes = new List<string> { ScopePrefix };

            if (includeSweepPrefix && !string.IsNullOrWhiteSpace(SweepPrefix))
            {
                prefixes.Add(SweepPrefix!);
            }

            return prefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(prefix => prefix.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private sealed class DiagnosticArtifactMatches
    {
        public DiagnosticArtifactMatches(
            List<Product> products,
            List<Review> reviews,
            List<Order> orders,
            List<OrderItem> orderItems,
            List<CartItem> cartItems)
        {
            Products = products;
            Reviews = reviews;
            Orders = orders;
            OrderItems = orderItems;
            CartItems = cartItems;
        }

        public List<Product> Products { get; }
        public List<Review> Reviews { get; }
        public List<Order> Orders { get; }
        public List<OrderItem> OrderItems { get; }
        public List<CartItem> CartItems { get; }

        public bool IsEmpty =>
            Products.Count == 0 &&
            Reviews.Count == 0 &&
            Orders.Count == 0 &&
            OrderItems.Count == 0 &&
            CartItems.Count == 0;

        public DiagnosticCleanupCounts ToCounts()
        {
            return new DiagnosticCleanupCounts
            {
                Products = Products.Count,
                Reviews = Reviews.Count,
                Orders = Orders.Count,
                OrderItems = OrderItems.Count,
                CartItems = CartItems.Count,
                CartSessions = CartItems
                    .Select(item => item.SessionId)
                    .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
            };
        }
    }
}
