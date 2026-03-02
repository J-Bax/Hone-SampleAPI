using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SampleApi.Data;
using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// Custom WebApplicationFactory that uses a real SQL Server LocalDB test database.
/// On creation the DB is dropped (if leftover from a crashed run) and recreated
/// with seed data. On disposal the test DB is cleaned up.
/// </summary>
public class SampleApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestDbName = "AutotuneSampleDb_Tests";
    private const string TestConnectionString =
        $"Server=(localdb)\\MSSQLLocalDB;Database={TestDbName};Trusted_Connection=True;MultipleActiveResultSets=true";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove the existing DbContext registration (points at the dev DB)
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));

            if (descriptor != null)
                services.Remove(descriptor);

            // Register with the dedicated test database
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlServer(TestConnectionString);
            });
        });

        builder.UseEnvironment("Development");
    }

    /// <summary>
    /// Called once before any test runs. Drops stale DB (crash resilience),
    /// recreates it, and seeds the canonical 1,000-product data set.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Drop first — handles leftover DB from a previous crashed run
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        SeedData.Initialize(db);
    }

    /// <summary>
    /// Called once after all tests complete. Drops the test database.
    /// </summary>
    public new async Task DisposeAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best-effort cleanup — next run will drop it anyway
        }

        await base.DisposeAsync();
    }
}
