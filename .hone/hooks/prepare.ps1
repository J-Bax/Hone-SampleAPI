[CmdletBinding()]
param(
    [string]$TargetPath = (Get-Location).Path,
    [hashtable]$Config = @{},
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $BaseUrl, $Experiment, $Config

$appSettingsPath = Join-Path -Path $TargetPath -ChildPath 'SampleApi\appsettings.json'
if (-not (Test-Path -Path $appSettingsPath)) {
    throw "App settings not found: $appSettingsPath"
}

$appSettings = Get-Content -Path $appSettingsPath -Raw | ConvertFrom-Json
$connectionString = $appSettings.ConnectionStrings.DefaultConnection
$serverMatch = [regex]::Match($connectionString, 'Server=([^;]+)')
$dbMatch = [regex]::Match($connectionString, 'Database=([^;]+)')

if (-not $serverMatch.Success -or -not $dbMatch.Success) {
    throw 'Failed to parse database connection string'
}

$sqlcmdPath = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmdPath) {
    throw 'sqlcmd not found in PATH'
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
    throw "sqlcmd exited with code ${exitCode}: $(($output | Out-String).Trim())"
}

Write-Host "Database '$dbName' dropped in $($stopwatch.Elapsed)."
