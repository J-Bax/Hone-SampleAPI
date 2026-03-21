@{
    Name       = 'SampleApi'
    BaseBranch = 'master'

    # ── Target API ──────────────────────────────────────────────
    Api = @{
        # Path to the .NET solution file (relative to target root)
        SolutionPath = 'SampleApi.sln'

        # Path to the API project directory (relative to target root)
        ProjectPath = 'SampleApi'

        # Subdirectories to scan for source code context (relative to ProjectPath)
        SourceCodePaths = @('Controllers', 'Data', 'Models', 'Pages')

        # File pattern for source files to include in analysis prompts
        SourceFileGlob = '*.cs'

        # Path to the E2E test project (relative to target root)
        TestProjectPath = 'SampleApi.Tests'

        # URL where the API listens when started.
        # Use port 0 for automatic ephemeral port assignment (recommended).
        BaseUrl = 'http://localhost:0'

        # Health check endpoint (GET, must return 200)
        HealthEndpoint = '/health'

        # Optional endpoint (POST) to trigger server-side GC between runs
        GcEndpoint = '/diag/gc'

        # Seconds to wait for API to become healthy after start
        StartupTimeout = 90

        # Directory for all performance results (relative to target root)
        ResultsPath = 'results'

        # Directory for optimization metadata (relative to target root)
        MetadataPath = 'results\metadata'
    }

    # ── Lifecycle Hooks ─────────────────────────────────────────
    Hooks = @{
        Prepare  = @{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
        Start    = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Stop     = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Ready    = @{ Type = 'Shared'; Name = 'health-poll' }
        Warmup   = @{ Type = 'Skip' }
        Active   = @{ Type = 'Shared'; Name = 'k6-run' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Cleanup  = @{ Type = 'Skip' }
    }

    # ── Scale Testing ───────────────────────────────────────────
    ScaleTest = @{
        # Path to the k6 scenario to run on each experiment (relative to target root)
        ScenarioPath = '.hone\scenarios\baseline.js'

        # JSON file listing all scenarios and their metadata
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'

        # Additional k6 CLI arguments
        ExtraArgs = @()

        # Run a short warmup pass before measured runs
        WarmupEnabled = $true
        WarmupScenarioPath = '.hone\scenarios\warmup.js'

        # Run the primary scenario this many times and take the median
        MeasuredRuns = 5

        # Seconds to pause between consecutive measured runs
        CooldownSeconds = 3
    }

    # ── .NET Performance Counters ───────────────────────────────
    DotnetCounters = @{
        # Enable counter collection during scale tests
        Enabled = $true

        # Counter providers to collect
        Providers = @(
            'System.Runtime'
            'Microsoft.AspNetCore.Hosting'
            'Microsoft.AspNetCore.Http.Connections'
            'System.Net.Http'
        )

        # Sampling interval in seconds
        RefreshIntervalSeconds = 1
    }
}
