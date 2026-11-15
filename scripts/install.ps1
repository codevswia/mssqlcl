<#
.SYNOPSIS
    Installs mssqlcl to %LOCALAPPDATA%\Programs\mssqlcl and adds it to User PATH.
    Supports downloading prebuilt GitHub Release ZIPs (no .NET SDK required),
    installing from a local ZIP (-LocalZip), or building from source (-FromSource).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\mssqlcl"),
    [string]$Version = "latest",
    [string]$LocalZip,
    [switch]$FromSource,
    [switch]$SelfContained,
    [string]$Repo = "codevswia/mssqlcl"
)

$ErrorActionPreference = "Stop"

function Add-ToUserPath {
    param([string]$Dir)
    $normalized = $Dir.TrimEnd('\')
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $segments = if ($currentPath) {
        $currentPath -split ";" | Where-Object { $_ -ne "" } | ForEach-Object { $_.TrimEnd('\') }
    } else {
        @()
    }

    if ($segments -notcontains $normalized) {
        $rawSegments = if ($currentPath) { $currentPath -split ";" | Where-Object { $_ -ne "" } } else { @() }
        $newPath = ($rawSegments + $normalized) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "      Added '$normalized' to User PATH (no Admin required)." -ForegroundColor Green
    } else {
        Write-Host "      '$normalized' is already in User PATH." -ForegroundColor Green
    }

    $sessionSegments = ($env:Path -split ";") | Where-Object { $_ -ne "" } | ForEach-Object { $_.TrimEnd('\') }
    if ($sessionSegments -notcontains $normalized) {
        $env:Path = "$($env:Path.TrimEnd(';'));$normalized"
    }
}

function Show-Completion {
    param([string]$Dir)
    $exePath = Join-Path $Dir "mssqlcl.exe"
    if (Test-Path $exePath) {
        $ver = & $exePath --version
        Write-Host "      Verified: $ver" -ForegroundColor Green
    }
    Write-Host ""
    Write-Host "Installation complete. Restart your IDE if it was already open so it picks up the updated PATH." -ForegroundColor Cyan
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

function Install-FromExtractedDir {
    param([string]$SourceDir, [string]$TargetDir)
    $exeSource = Join-Path $SourceDir "mssqlcl.exe"
    if (-not (Test-Path $exeSource)) {
        throw "mssqlcl.exe not found inside '$SourceDir'."
    }

    $resolvedSource = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    $resolvedTarget = (Resolve-Path $TargetDir).Path.TrimEnd('\')

    if ($resolvedSource -ine $resolvedTarget) {
        Copy-Item -Path $exeSource -Destination (Join-Path $resolvedTarget "mssqlcl.exe") -Force
        foreach ($doc in @("README.md", "LICENSE")) {
            $docPath = Join-Path $resolvedSource $doc
            if (Test-Path $docPath) {
                Copy-Item -Path $docPath -Destination (Join-Path $resolvedTarget $doc) -Force
            }
        }
    }
}

if ($FromSource) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $projectPath = Join-Path $repoRoot "src\mssqlcl\mssqlcl.csproj"

    Write-Host "[1/4] Checking .NET 10 SDK and source project..." -ForegroundColor Cyan
    if (-not (Test-Path $projectPath)) {
        throw "Project file not found at $projectPath"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET 10 SDK is required for -FromSource. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 or omit -FromSource to install a prebuilt release."
    }
    $sdks = & dotnet --list-sdks 2>$null
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        throw ".NET 10 SDK (10.x) was not found in 'dotnet --list-sdks'. Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 or use the prebuilt self-contained ZIP."
    }

    Write-Host "[2/4] Publishing mssqlcl from source to '$InstallDir'..." -ForegroundColor Cyan
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

    Write-Host "[3/4] Updating User PATH..." -ForegroundColor Cyan
    Add-ToUserPath -Dir $InstallDir

    Write-Host "[4/4] Verifying installation..." -ForegroundColor Cyan
    Show-Completion -Dir $InstallDir
    return
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("mssqlcl-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    $zipToExtract = $LocalZip
    if ($zipToExtract) {
        Write-Host "[1/4] Checking local ZIP '$zipToExtract'..." -ForegroundColor Cyan
        if (-not (Test-Path $zipToExtract)) {
            throw "Local ZIP not found at '$zipToExtract'."
        }
    } else {
        $releaseApi = if ($Version -eq "latest") {
            "https://api.github.com/repos/$Repo/releases/latest"
        } else {
            "https://api.github.com/repos/$Repo/releases/tags/$Version"
        }

        Write-Host "[1/4] Fetching release metadata and downloading from $Repo ($Version)..." -ForegroundColor Cyan
        $headers = @{ "User-Agent" = "mssqlcl-installer" }
        $release = Invoke-RestMethod -Uri $releaseApi -Headers $headers
        $zipAsset = $release.assets | Where-Object { $_.name -like "*-win-x64.zip" } | Select-Object -First 1
        if (-not $zipAsset) {
            throw "No win-x64 ZIP asset found in release '$($release.tag_name)'."
        }

        $zipToExtract = Join-Path $tempDir $zipAsset.name
        Write-Host "      Downloading $($zipAsset.name)..." -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipToExtract -Headers $headers

        $checksumAsset = $release.assets | Where-Object { $_.name -eq "checksums.txt" } | Select-Object -First 1
        if ($checksumAsset) {
            $checksumFile = Join-Path $tempDir "checksums.txt"
            Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $checksumFile -Headers $headers
            $expectedLine = Get-Content $checksumFile | Where-Object { $_ -match [regex]::Escape($zipAsset.name) } | Select-Object -First 1
            if ($expectedLine) {
                $expectedHash = ($expectedLine -split '\s+')[0].Trim().ToLowerInvariant()
                $actualHash = (Get-FileHash -Path $zipToExtract -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($expectedHash -ne $actualHash) {
                    throw "SHA-256 checksum mismatch for $($zipAsset.name). Expected $expectedHash, got $actualHash."
                }
                Write-Host "      SHA-256 checksum verified." -ForegroundColor Green
            }
        }
    }

    Write-Host "[2/4] Extracting and installing to '$InstallDir'..." -ForegroundColor Cyan
    $extractDir = Join-Path $tempDir "extracted"
    Expand-Archive -Path $zipToExtract -DestinationPath $extractDir -Force
    Install-FromExtractedDir -SourceDir $extractDir -TargetDir $InstallDir

    Write-Host "[3/4] Updating User PATH..." -ForegroundColor Cyan
    Add-ToUserPath -Dir $InstallDir

    Write-Host "[4/4] Verifying installation..." -ForegroundColor Cyan
    Show-Completion -Dir $InstallDir
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
