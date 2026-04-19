namespace SampleApi.Models;

public class DiagnosticRunRequest
{
    public string RunId { get; set; } = string.Empty;
    public string? Scenario { get; set; }
    public string? ScopePrefix { get; set; }
    public string? SweepPrefix { get; set; }
}

public class DiagnosticCleanupCounts
{
    public int Products { get; set; }
    public int Reviews { get; set; }
    public int Orders { get; set; }
    public int OrderItems { get; set; }
    public int CartItems { get; set; }
    public int CartSessions { get; set; }
}

public class DiagnosticCatalogSnapshot
{
    public int ProductCount { get; set; }
    public int CategoryCount { get; set; }
    public List<string> Categories { get; set; } = new();
}

public class DiagnosticRunResponse
{
    public string Mode { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string? Scenario { get; set; }
    public string ScopePrefix { get; set; } = string.Empty;
    public string? SweepPrefix { get; set; }
    public string MatchMode { get; set; } = string.Empty;
    public DiagnosticCleanupCounts Removed { get; set; } = new();
    public DiagnosticCleanupCounts Remaining { get; set; } = new();
    public DiagnosticCatalogSnapshot Catalog { get; set; } = new();
}
