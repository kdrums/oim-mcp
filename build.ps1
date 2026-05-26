#Requires -Version 5.1
<#
.SYNOPSIS
    Wrapper that delegates to eng\build.ps1.
.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -SignToolPath "C:\..." -CodeSigningCertificateThumbprint "<thumbprint>" -RequireSignature
#>
[CmdletBinding()]
param(
    [string]$SignToolPath,
    [string]$CodeSigningCertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$RequireSignature
)

& (Join-Path $PSScriptRoot 'eng\build.ps1') @PSBoundParameters
exit $LASTEXITCODE
