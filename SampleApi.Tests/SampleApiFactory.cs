using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SampleApi.Data;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// Custom WebApplicationFactory that replaces SQL Server with an in-memory database
/// for E2E testing. This allows tests to run without a real database connection.
/// </summary>
public class SampleApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "TestDb_" + Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove the existing DbContext registration
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));

            if (descriptor != null)
                services.Remove(descriptor);

            // Add an in-memory database for testing
            // Use a fixed name per factory instance so all scopes share the same DB
            var dbName = _dbName;
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(dbName);
            });
        });

        builder.UseEnvironment("Development");
    }
}
