<#
.SYNOPSIS
    Builds and installs mssqlcl to %LOCALAPPDATA%\Programs\mssqlcl and adds it to User PATH.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\mssqlcl"),
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\mssqlcl\mssqlcl.csproj"

if (-not (Test-Path $projectPath)) {
    throw "Project file not found at $projectPath"
}

Write-Host "Publishing mssqlcl to '$InstallDir'..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

$publishArgs = @(
    "publish", $projectPath,
    "-c", "Release",
    "-r", "win-x64",
    "-o", $InstallDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true"
)

if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
} else {
    $publishArgs += @("--self-contained", "false")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
$segments = if ($currentPath) { $currentPath -split ";" | Where-Object { $_ -ne "" } } else { @() }
if ($segments -notcontains $InstallDir) {
    $newPath = ($segments + $InstallDir) -join ";"
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    Write-Host "Added '$InstallDir' to User PATH. Restart your terminal/IDE to pick up the change." -ForegroundColor Green
} else {
    Write-Host "'$InstallDir' is already in User PATH." -ForegroundColor Green
}

Write-Host ""
Write-Host "Installation complete. Example MCP client configuration:" -ForegroundColor Cyan
Write-Host @"
{
  "mcpServers": {
    "mssqlcl": {
      "command": "mssqlcl",
      "args": ["--mcp"]
    }
  }
}
"@
