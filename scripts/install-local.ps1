<#
.SYNOPSIS
    Offline installer for mssqlcl from an extracted release ZIP directory.
    Copies mssqlcl.exe to %LOCALAPPDATA%\Programs\mssqlcl and adds it to User PATH.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\mssqlcl"),
    [string]$SourceDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$exeSource = Join-Path $SourceDir "mssqlcl.exe"
if (-not (Test-Path $exeSource)) {
    throw "mssqlcl.exe not found in '$SourceDir'. Extract the full release ZIP first."
}

Write-Host "Installing mssqlcl to '$InstallDir'..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Copy-Item -Path $exeSource -Destination (Join-Path $InstallDir "mssqlcl.exe") -Force
foreach ($doc in @("README.md", "LICENSE")) {
    $docPath = Join-Path $SourceDir $doc
    if (Test-Path $docPath) {
        Copy-Item -Path $docPath -Destination (Join-Path $InstallDir $doc) -Force
    }
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

$installedExe = Join-Path $InstallDir "mssqlcl.exe"
$ver = & $installedExe --version
Write-Host "Verified: $ver" -ForegroundColor Green
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
