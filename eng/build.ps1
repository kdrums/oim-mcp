#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and publishes McpServerOneIdentityApi as a self-contained Windows x64 executable.

.DESCRIPTION
    Reads the version from McpServerOneIdentityApi.csproj, runs dotnet publish, and places
    the self-contained executable under artifacts\dist\<version>\ ready to copy to the
    Software Center source share.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Verbose

.EXAMPLE
    .\build.ps1 -SignToolPath "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe" `
                -CodeSigningCertificateThumbprint "<thumbprint>" -RequireSignature
#>
[CmdletBinding()]
param(
    [string]$SignToolPath,
    [string]$CodeSigningCertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$RequireSignature
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src\McpServerOneIdentityApi'
$csprojPath = Join-Path $projectDir 'McpServerOneIdentityApi.csproj'

if (-not (Test-Path $csprojPath)) {
    Write-Error "Project file not found: $csprojPath"
    exit 1
}

# ---------------------------------------------------------------------------
# Read version from .csproj
# ---------------------------------------------------------------------------
[xml]$csproj = Get-Content $csprojPath
$version = $csproj.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Error "Could not read <Version> from $csprojPath"
    exit 1
}

Write-Host "Version : $version"

$sourceRevision = 'unknown'
$sourceDirty    = $false
if (Get-Command git -ErrorAction SilentlyContinue) {
    $gitRevision = & git -C $repoRoot rev-parse --short=12 HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitRevision)) {
        $sourceRevision = $gitRevision.Trim()
    }

    $gitStatus = & git -C $repoRoot status --porcelain -- . ':(exclude)artifacts' 2>$null
    $sourceDirty = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($gitStatus -join ''))
}

Write-Host "Revision: $sourceRevision"
if ($sourceDirty) {
    Write-Warning "The repository has uncommitted changes. The release manifest will mark sourceDirty=true."
}

# ---------------------------------------------------------------------------
# Verify dotnet SDK is available
# ---------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK not found. Download from https://aka.ms/dotnet/download (requires .NET 9 SDK)"
    exit 1
}

$sdkVersion = & dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
    Write-Error "dotnet is installed but no SDK was found (runtime only). Install the .NET 9 SDK from https://aka.ms/dotnet/download"
    exit 1
}

Write-Host "SDK     : $sdkVersion"

if (-not $sdkVersion.StartsWith('9.')) {
    Write-Warning "Expected .NET 9 SDK - found $sdkVersion. Build may still succeed but is untested on other versions."
}

# ---------------------------------------------------------------------------
# Output paths
# ---------------------------------------------------------------------------
$distDir    = Join-Path $repoRoot "artifacts\dist\$version"
$publishDir = Join-Path $repoRoot "artifacts\publish\$version"

foreach ($dir in @($distDir, $publishDir)) {
    if (Test-Path $dir) {
        Write-Verbose "Cleaning: $dir"
        Remove-Item $dir -Recurse -Force
    }
    New-Item $dir -ItemType Directory -Force | Out-Null
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
Write-Host "Publishing..."

$publishArgs = @(
    'publish'
    $csprojPath
    '--configuration', 'Release'
    '--runtime',       'win-x64'
    '--self-contained', 'true'
    '--output',        $publishDir
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=None'
    '-p:DebugSymbols=false'
    '-p:ContinuousIntegrationBuild=true'
    "-p:SourceRevisionId=$sourceRevision"
    "-p:InformationalVersion=$version+$sourceRevision"
    '--nologo'
)

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------------------
# Assemble dist folder
# ---------------------------------------------------------------------------
$exeName    = 'McpServerOneIdentityApi.exe'
$exePath    = Join-Path $publishDir $exeName

if (-not (Test-Path $exePath)) {
    Write-Error "Expected output not found: $exePath"
    exit 1
}

$distExePath = Join-Path $distDir $exeName
Copy-Item $exePath $distExePath

# Write version file used by Software Center detection rules
$version | Set-Content (Join-Path $distDir 'version.txt') -Encoding UTF8

# ---------------------------------------------------------------------------
# Optional Authenticode signing
# ---------------------------------------------------------------------------
if ($CodeSigningCertificateThumbprint) {
    if (-not $SignToolPath) {
        $signtoolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($signtoolCommand) { $SignToolPath = $signtoolCommand.Source }
    }

    if (-not $SignToolPath -or -not (Test-Path $SignToolPath)) {
        Write-Error "signtool.exe was not found. Provide -SignToolPath or install the Windows SDK."
        exit 1
    }

    Write-Host "Signing executable..."
    $signArgs = @(
        'sign'
        '/fd', 'SHA256'
        '/td', 'SHA256'
        '/tr', $TimestampUrl
        '/sha1', $CodeSigningCertificateThumbprint
        $distExePath
    )

    & $SignToolPath @signArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "signtool failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
} else {
    Write-Warning "Signing skipped. For release builds, use -CodeSigningCertificateThumbprint <thumbprint> -RequireSignature."
}

$signature = Get-AuthenticodeSignature $distExePath

if ($RequireSignature -and $signature.Status -ne 'Valid') {
    Write-Error "Release signing is required but signature status is '$($signature.Status)'."
    exit 1
}

$hash = Get-FileHash $distExePath -Algorithm SHA256
$hash.Hash | Set-Content (Join-Path $distDir "$exeName.sha256") -Encoding ASCII

$manifest = [ordered]@{
    name                        = 'McpServerOneIdentityApi'
    version                     = $version
    sourceRevision              = $sourceRevision
    sourceDirty                 = $sourceDirty
    builtAtUtc                  = (Get-Date).ToUniversalTime().ToString('o')
    runtime                     = 'win-x64'
    selfContained               = $true
    sha256                      = $hash.Hash
    signatureStatus             = $signature.Status.ToString()
    signerCertificateThumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
    repositoryUrl               = 'https://github.com/kdrums/claude-oim-mcp.git'
}

$manifest | ConvertTo-Json -Depth 5 |
    Set-Content (Join-Path $distDir 'release.json') -Encoding UTF8

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
$exeSize = [math]::Round((Get-Item $distExePath).Length / 1MB, 1)

Write-Host ""
Write-Host "Build complete" -ForegroundColor Green
Write-Host "  Version    : $version"
Write-Host "  Revision   : $sourceRevision"
Write-Host "  SHA256     : $($hash.Hash)"
Write-Host "  Signature  : $($signature.Status)"
Write-Host "  Output     : $distDir"
Write-Host "  Exe size   : ${exeSize} MB"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Copy artifacts\dist\$version\ to your Software Center source share, e.g.:"
Write-Host "       \\software-source\Sources\Apps\McpOneIdentityApi\$version\"
Write-Host "  2. Install command:"
Write-Host "       McpServerOneIdentityApi.exe --install ``"
Write-Host "         --oim-base-url https://oimserver/AppServer ``"
Write-Host "         --token-endpoint https://oimserver/rsts/oauth2/token ``"
Write-Host "         --client-id <id> --client-secret <secret> --scope openid"
Write-Host "  3. Uninstall command:"
Write-Host "       McpServerOneIdentityApi.exe --uninstall"
Write-Host "  4. Detection file:"
Write-Host "       %LOCALAPPDATA%\Programs\McpServerOneIdentityApi\version.txt = $version"
