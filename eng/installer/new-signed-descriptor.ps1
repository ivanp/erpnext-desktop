#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates a signed QEMU runtime descriptor (runtime-descriptor.signed.json)
    for Serpy.InstallerBootstrapper and MSI packaging.

.DESCRIPTION
    Reads QEMU metadata from config/versions.yaml and signs
    RuntimeDescriptor.CanonicalBytes() using an RSA private key with
    RSA-PSS/SHA-256. The resulting JSON matches SignedRuntimeDescriptorFile
    and is verified against the public key / compiled-in trust anchor.

    Works under both Windows PowerShell 5.1 (powershell.exe) and pwsh
    by delegating to the .NET 10 DescriptorSigner CLI.
#>
[CmdletBinding()]
param(
    [string]$VersionsYamlPath,
    [string]$PrivateKeyPath,
    [string]$OutputPath,
    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $PSScriptRoot "DescriptorSigner\DescriptorSigner.csproj"

if (-not $VersionsYamlPath) {
    $VersionsYamlPath = Join-Path $repoRoot "config\versions.yaml"
}
if (-not $PrivateKeyPath) {
    $PrivateKeyPath = Join-Path $repoRoot ".local-signing\descriptor-signing-private.pem"
}
if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "dist\runtime-descriptor.signed.json"
}

if (-not (Test-Path -LiteralPath $VersionsYamlPath)) {
    throw "versions.yaml not found: $VersionsYamlPath"
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath)) {
    throw "Descriptor signing private key not found: $PrivateKeyPath`nFor local dev, generate one using: ./eng/gen-descriptor-signing-key.sh"
}

$resolvedVersions = (Resolve-Path -LiteralPath $VersionsYamlPath).Path
$resolvedKey = (Resolve-Path -LiteralPath $PrivateKeyPath).Path

$argsList = @(
    'run',
    '--project', $projectPath,
    '-c', 'Release',
    '--',
    '--versions', $resolvedVersions,
    '--key', $resolvedKey,
    '--out', $OutputPath
)
if ($Verify) {
    $argsList += '--verify'
}

& dotnet @argsList
if ($LASTEXITCODE -ne 0) {
    throw "Descriptor signing failed with exit code $LASTEXITCODE."
}
