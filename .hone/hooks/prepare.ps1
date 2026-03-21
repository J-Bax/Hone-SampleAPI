<#
.SYNOPSIS
    Resets the sample API database to ensure clean state between experiments.

.DESCRIPTION
    Drops the target database so that the next API startup recreates it from
    scratch with fresh seed data.
    This ensures every experiment starts with identical data for fair
    performance comparisons.

.PARAMETER TargetPath
    Root directory of the target project (the sample-api checkout).

.PARAMETER Config
    Parsed .hone/config.psd1 hashtable.

.PARAMETER BaseUrl
    The base URL where the API will be started (unused by this hook).

.PARAMETER Experiment
    Current experiment number for logging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TargetPath,

    [Parameter(Mandatory)]
    [hashtable]$Config,

    [string]$BaseUrl,
    [int]$Experiment = 0
)

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# ── Parse connection string from appsettings.json ───────────────────────────
$appSettingsPath = Join-Path -Path $TargetPath -ChildPath $Config.Api.ProjectPath 'appsettings.json'

if (-not (Test-Path $appSettingsPath)) {
    $stopwatch.Stop()
    return [PSCustomObject]@{
        Success   = $false
        Message   = "appsettings.json not found at: $appSettingsPath"
        Duration  = $stopwatch.Elapsed
        Artifacts = @()
    }
}

$appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
$connectionString = $appSettings.ConnectionStrings.DefaultConnection

$serverMatch = [regex]::Match($connectionString, 'Server=([^;]+)')
$dbMatch = [regex]::Match($connectionString, 'Database=([^;]+)')

if (-not $serverMatch.Success -or -not $dbMatch.Success) {
    $stopwatch.Stop()
    return [PSCustomObject]@{
        Success   = $false
        Message   = "Could not parse connection string: $connectionString"
        Duration  = $stopwatch.Elapsed
        Artifacts = @()
    }
}

$server = $serverMatch.Groups[1].Value
$dbName = $dbMatch.Groups[1].Value

# Escape closing brackets to prevent SQL injection
$dbName = $dbName.Replace(']', ']]')

# ── Drop the database via sqlcmd ────────────────────────────────────────────
$dropQuery = @"
IF DB_ID('$dbName') IS NOT NULL
BEGIN
    ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$dbName];
END
"@

try {
    $sqlcmdPath = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if (-not $sqlcmdPath) {
        $stopwatch.Stop()
        return [PSCustomObject]@{
            Success   = $false
            Message   = 'sqlcmd not found in PATH. Install SQL Server command-line tools.'
            Duration  = $stopwatch.Elapsed
            Artifacts = @()
        }
    }

    $output = & sqlcmd -S $server -Q $dropQuery -b 2>&1
    $exitCode = $LASTEXITCODE

    $stopwatch.Stop()

    if ($exitCode -ne 0) {
        Write-Verbose "sqlcmd exited with code $exitCode — database may not have existed"
    }

    return [PSCustomObject]@{
        Success   = $true
        Message   = "Database '$dbName' dropped — will be recreated on next API startup"
        Duration  = $stopwatch.Elapsed
        Artifacts = @()
    }
} catch {
    $stopwatch.Stop()
    return [PSCustomObject]@{
        Success   = $false
        Message   = "Failed to reset database: $_"
        Duration  = $stopwatch.Elapsed
        Artifacts = @()
    }
}
