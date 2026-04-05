[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $BaseUrl, $Experiment, $Config

$appSettingsPath = Join-Path -Path $TargetPath -ChildPath 'SampleApi\appsettings.json'
if (-not (Test-Path -Path $appSettingsPath)) {
    return [PSCustomObject]@{
        Success = $false
        Message = "App settings not found: $appSettingsPath"
        Duration = [timespan]::Zero
        Artifacts = @()
    }
}

$appSettings = Get-Content -Path $appSettingsPath -Raw | ConvertFrom-Json
$connectionString = $appSettings.ConnectionStrings.DefaultConnection
$serverMatch = [regex]::Match($connectionString, 'Server=([^;]+)')
$dbMatch = [regex]::Match($connectionString, 'Database=([^;]+)')

if (-not $serverMatch.Success -or -not $dbMatch.Success) {
    return [PSCustomObject]@{
        Success = $false
        Message = 'Failed to parse database connection string'
        Duration = [timespan]::Zero
        Artifacts = @()
    }
}

$sqlcmdPath = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmdPath) {
    return [PSCustomObject]@{
        Success = $false
        Message = 'sqlcmd not found in PATH'
        Duration = [timespan]::Zero
        Artifacts = @()
    }
}

$server = $serverMatch.Groups[1].Value
$dbName = $dbMatch.Groups[1].Value.Replace(']', ']]')
$dropQuery = @"
IF DB_ID('$dbName') IS NOT NULL
BEGIN
    ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$dbName];
END
"@

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$output = & sqlcmd -S $server -Q $dropQuery -b 2>&1
$exitCode = $LASTEXITCODE
$stopwatch.Stop()

if ($exitCode -ne 0) {
    return [PSCustomObject]@{
        Success = $false
        Message = "sqlcmd exited with code $exitCode: $(($output | Out-String).Trim())"
        Duration = $stopwatch.Elapsed
        Artifacts = @()
    }
}

return [PSCustomObject]@{
    Success = $true
    Message = "Database '$dbName' dropped"
    Duration = $stopwatch.Elapsed
    Artifacts = @()
    Database = $dbName
}
