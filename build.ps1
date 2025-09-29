[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$GTFOPath,

    [Parameter(Position = 1)]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipCopy,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $repoRoot) {
    $repoRoot = Get-Location
}

if ([string]::IsNullOrWhiteSpace($GTFOPath)) {
    $GTFOPath = $env:GTFO_PATH
}

if ([string]::IsNullOrWhiteSpace($GTFOPath)) {
    throw 'GTFO path not specified. Use -GTFOPath or set GTFO_PATH environment variable.'
}

try {
    $resolvedGTFOPath = (Resolve-Path -Path $GTFOPath -ErrorAction Stop).ProviderPath
} catch {
    throw "Failed to resolve GTFO path '$GTFOPath'."
}

if (-not (Test-Path -Path $resolvedGTFOPath -PathType Container)) {
    throw "GTFO path '$resolvedGTFOPath' is not a directory."
}

$env:GTFO_PATH = $resolvedGTFOPath

if (-not (Get-Command -Name dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI not found. Install the .NET 6 SDK and ensure dotnet is on PATH.'
}

$projectPath = Join-Path -Path $repoRoot -ChildPath 'GTFO_VR\GTFO_VR.csproj'
if (-not (Test-Path -Path $projectPath)) {
    throw "Project file not found at '$projectPath'."
}

$propertyArgs = @("-p:GTFO_PATH=$resolvedGTFOPath")
if ($SkipCopy.IsPresent) {
    $propertyArgs += '-p:DisablePostBuildCopy=true'
}

$dotnetArgs = @('build', $projectPath, '-c', $Configuration) + $propertyArgs
if ($NoRestore.IsPresent) {
    $dotnetArgs += '--no-restore'
}

Write-Host "Building GTFO_VR ($Configuration)"
Write-Host "GTFO_PATH: $resolvedGTFOPath"
if ($SkipCopy) {
    Write-Host 'Post-build copying has been disabled.'
}

& dotnet @dotnetArgs
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    throw "dotnet build failed with exit code $exitCode."
}

Write-Host 'Build completed successfully.'
