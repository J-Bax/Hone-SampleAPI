@{
    Name = 'SampleApi'
    BaseBranch = 'master'

    Api = @{
        SolutionPath = 'SampleApi.sln'
        ProjectPath = 'SampleApi'
        TestProjectPath = 'SampleApi.Tests'
        SourceCodePaths = @('Controllers', 'Data', 'Models', 'Pages')
        SourceFileGlob = '*.cs'
        BaseUrl = 'http://localhost:0'
        HealthEndpoint = '/health'
        GcEndpoint = '/diag/gc'
        StartupTimeout = 90
        ResultsPath = '.hone\results'
        MetadataPath = '.hone\results\metadata'
    }

    Hooks = @{
        Prepare = @{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
        Start = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Stop = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Ready = @{ Type = 'Shared'; Name = 'health-poll' }
        Warmup = @{ Type = 'Skip' }
        Active = @{ Type = 'Shared'; Name = 'k6-run' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Cleanup = @{ Type = 'Skip' }
    }

    ScaleTest = @{
        ScenarioPath = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        WarmupEnabled = $true
        WarmupScenarioPath = '.hone\scenarios\warmup.js'
        MeasuredRuns = 5
        CooldownSeconds = 3
    }
}
