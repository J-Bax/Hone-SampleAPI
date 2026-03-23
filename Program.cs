using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SampleApi.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Services ───────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContextPool<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => sqlOptions.EnableRetryOnFailure()), poolSize: 1024);

var app = builder.Build();

// ── Middleware Pipeline ────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

// ── Health Check (simple) ──────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// ── Diagnostic: force GC (perf-testing only) ──────────────────────────────
// Allows the harness to trigger a full blocking GC between measured runs
// so heap pressure from run N doesn't contaminate run N+1.
app.MapPost("/diag/gc", () =>
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    return Results.Ok(new { collected = true, totalMemoryMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0 });
});

// ── Database Initialization ────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    SeedData.Initialize(db);
}

app.Run();

// Make the implicit Program class public so test projects can access it
public partial class Program { }
