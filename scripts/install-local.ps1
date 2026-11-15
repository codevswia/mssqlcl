<#
.SYNOPSIS
    Offline installer for mssqlcl from an extracted release ZIP directory.
    Copies mssqlcl.exe to %LOCALAPPDATA%\Programs\mssqlcl and adds it to User PATH.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\mssqlcl"),
    [string]$SourceDir,
    [switch]$PauseOnExit
)

$ErrorActionPreference = "Stop"

try {
    if ([string]::IsNullOrWhiteSpace($SourceDir)) {
        $SourceDir = $PSScriptRoot
    }
    Write-Host "[1/4] Checking package files in '$SourceDir'..." -ForegroundColor Cyan
    $exeSource = Join-Path $SourceDir "mssqlcl.exe"
    if (-not (Test-Path $exeSource)) {
        throw "mssqlcl.exe not found in '$SourceDir'. Extract the full release ZIP first."
    }

    $resolvedSource = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $resolvedTarget = (Resolve-Path $InstallDir).Path.TrimEnd('\')

    Write-Host "[2/4] Installing binaries to '$resolvedTarget'..." -ForegroundColor Cyan
    if ($resolvedSource -ieq $resolvedTarget) {
        Write-Host "      Source and target directories are the same; keeping files in place." -ForegroundColor DarkGray
    } else {
        Copy-Item -Path $exeSource -Destination (Join-Path $resolvedTarget "mssqlcl.exe") -Force
        foreach ($doc in @("README.md", "LICENSE")) {
            $docPath = Join-Path $resolvedSource $doc
            if (Test-Path $docPath) {
                Copy-Item -Path $docPath -Destination (Join-Path $resolvedTarget $doc) -Force
            }
        }
    }

    Write-Host "[3/4] Updating User PATH..." -ForegroundColor Cyan
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $segments = if ($currentPath) {
        $currentPath -split ";" | Where-Object { $_ -ne "" } | ForEach-Object { $_.TrimEnd('\') }
    } else {
        @()
    }

    if ($segments -notcontains $resolvedTarget) {
        $rawSegments = if ($currentPath) { $currentPath -split ";" | Where-Object { $_ -ne "" } } else { @() }
        $newPath = ($rawSegments + $resolvedTarget) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "      Added '$resolvedTarget' to User PATH (no Admin required)." -ForegroundColor Green
    } else {
        Write-Host "      '$resolvedTarget' is already in User PATH." -ForegroundColor Green
    }

    $sessionSegments = ($env:Path -split ";") | Where-Object { $_ -ne "" } | ForEach-Object { $_.TrimEnd('\') }
    if ($sessionSegments -notcontains $resolvedTarget) {
        $env:Path = "$($env:Path.TrimEnd(';'));$resolvedTarget"
    }

    Write-Host "[4/4] Verifying installation..." -ForegroundColor Cyan
    $installedExe = Join-Path $resolvedTarget "mssqlcl.exe"
    $ver = & $installedExe --version
    Write-Host "      Verified: $ver" -ForegroundColor Green
    Write-Host ""
    Write-Host "Installation complete. Restart your IDE/terminal if it was already open." -ForegroundColor Cyan
    Write-Host "Example MCP client configuration:" -ForegroundColor Cyan
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
}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    if ($PauseOnExit) {
        Write-Host ""
        Read-Host "Press Enter to close"
    }
    exit 1
}

if ($PauseOnExit) {
    Write-Host ""
    Read-Host "Press Enter to close"
}
