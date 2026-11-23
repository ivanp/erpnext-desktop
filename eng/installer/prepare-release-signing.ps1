#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prepares signing assets and options for the Serpy release workflow.

.DESCRIPTION
    Inspects signing environment variables (DESCRIPTOR_SIGNING_KEY,
    DESCRIPTOR_TRUST_KEY, SIGNING_CERT_PFX_BASE64, SIGNING_CERT_PASSWORD).
    If signing secrets are provided, decodes them into a process-isolated
    temporary directory strictly outside the repository workspace, generates
    dist/runtime-descriptor.signed.json using new-signed-descriptor.ps1,
    and configures MSBuild trust properties. If secrets are absent, falls
    back gracefully to -AllowMissingDescriptor for unsigned dev/staging runs.
#>
[CmdletBinding()]
param(
    [string]$DescriptorSigningKey = $env:DESCRIPTOR_SIGNING_KEY,
    [string]$DescriptorTrustKey = $env:DESCRIPTOR_TRUST_KEY,
    [string]$SigningCertPfxBase64 = $env:SIGNING_CERT_PFX_BASE64,
    [string]$SigningCertPassword = $env:SIGNING_CERT_PASSWORD,
    [string]$VersionsYamlPath,
    [string]$DistDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $VersionsYamlPath) {
    $VersionsYamlPath = Join-Path $repoRoot "config\versions.yaml"
} elseif (-not [System.IO.Path]::IsPathRooted($VersionsYamlPath)) {
    $VersionsYamlPath = Join-Path $repoRoot $VersionsYamlPath
}

if (-not $DistDir) {
    $DistDir = Join-Path $repoRoot "dist"
} elseif (-not [System.IO.Path]::IsPathRooted($DistDir)) {
    $DistDir = Join-Path $repoRoot $DistDir
}

# Process-isolated temp directory strictly outside repository and publish/dist trees
$baseTemp = if ($env:RUNNER_TEMP) {
    Join-Path $env:RUNNER_TEMP "signing"
} else {
    Join-Path ([System.IO.Path]::GetTempPath()) "serpy-signing"
}
$tempSigningDir = Join-Path $baseTemp ([System.Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $tempSigningDir | Out-Null

$descriptorPath = ""
$allowMissingDescriptor = $true
$extraMsbuildArgs = ""
$signingCertPfx = ""
$signingCertPasswordPath = ""
$signingCertArgs = ""

# 1. Runtime Descriptor Signing
if (-not [string]::IsNullOrWhiteSpace($DescriptorSigningKey)) {
    Write-Host "DESCRIPTOR_SIGNING_KEY detected. Generating signed runtime descriptor..."
    $tempPemPath = Join-Path $tempSigningDir "descriptor-signing-private.pem"
    [System.IO.File]::WriteAllText($tempPemPath, $DescriptorSigningKey)

    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
    $descriptorOutputPath = Join-Path $DistDir "runtime-descriptor.signed.json"

    try {
        $newSignedDescriptorScript = Join-Path $PSScriptRoot "new-signed-descriptor.ps1"
        & $newSignedDescriptorScript `
            -VersionsYamlPath $VersionsYamlPath `
            -PrivateKeyPath $tempPemPath `
            -OutputPath $descriptorOutputPath `
            -Verify
        if ($LASTEXITCODE -ne 0) {
            throw "new-signed-descriptor.ps1 failed with exit code $LASTEXITCODE."
        }
    } finally {
        # Securely remove temporary private key PEM immediately after descriptor signing
        Remove-Item -LiteralPath $tempPemPath -Force -ErrorAction SilentlyContinue
    }

    $descriptorPath = "dist/runtime-descriptor.signed.json"
    $allowMissingDescriptor = $false
    Write-Host "Signed runtime descriptor generated successfully at $descriptorPath."
} else {
    Write-Host "DESCRIPTOR_SIGNING_KEY not detected; falling back to -AllowMissingDescriptor (unsigned dev packaging mode)."
    $descriptorPath = ""
    $allowMissingDescriptor = $true
}

# 2. Production Trust Anchor MSBuild Properties
if (-not [string]::IsNullOrWhiteSpace($DescriptorTrustKey)) {
    Write-Host "DESCRIPTOR_TRUST_KEY detected. Configuring production trust key for MSBuild."
    $trimmedTrustKey = $DescriptorTrustKey.Trim()
    $extraMsbuildArgs = "/p:SerpyDescriptorTrustKey=$trimmedTrustKey /p:SerpyReleasePublish=true"
} else {
    Write-Host "DESCRIPTOR_TRUST_KEY is not set. MSBuild will use default/local trust key if available."
}

# 3. Authenticode Code Signing Certificate
if (-not [string]::IsNullOrWhiteSpace($SigningCertPfxBase64)) {
    Write-Host "SIGNING_CERT_PFX_BASE64 detected. Preparing Authenticode signing certificate..."
    $tempPfxPath = Join-Path $tempSigningDir "authenticode-cert.pfx"
    $bytes = [System.Convert]::FromBase64String($SigningCertPfxBase64.Trim())
    [System.IO.File]::WriteAllBytes($tempPfxPath, $bytes)
    $signingCertPfx = $tempPfxPath

    $certArgsList = @("-SigningCertPfx `"$signingCertPfx`"")
    if (-not [string]::IsNullOrEmpty($SigningCertPassword)) {
        $tempPwdPath = Join-Path $tempSigningDir "authenticode-cert.pwd"
        [System.IO.File]::WriteAllText($tempPwdPath, $SigningCertPassword)
        $signingCertPasswordPath = $tempPwdPath
        $certArgsList += "-SigningCertPasswordPath `"$signingCertPasswordPath`""
    }
    $signingCertArgs = $certArgsList -join " "
    Write-Host "Authenticode certificate prepared at $signingCertPfx."
} else {
    Write-Host "SIGNING_CERT_PFX_BASE64 is not set; Authenticode code signing will be skipped."
}

# 4. GitHub Actions Output
if ($env:GITHUB_OUTPUT) {
    @(
        "descriptor_path=$descriptorPath"
        "allow_missing_descriptor=$($allowMissingDescriptor.ToString().ToLowerInvariant())"
        "extra_msbuild_args=$extraMsbuildArgs"
        "signing_cert_pfx=$signingCertPfx"
        "signing_cert_password_path=$signingCertPasswordPath"
        "signing_cert_args=$signingCertArgs"
        "has_signing_cert=$((-not [string]::IsNullOrEmpty($signingCertPfx)).ToString().ToLowerInvariant())"
    ) | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

[PSCustomObject]@{
    DescriptorPath          = $descriptorPath
    AllowMissingDescriptor  = $allowMissingDescriptor
    ExtraMsbuildArgs        = $extraMsbuildArgs
    SigningCertPfx          = $signingCertPfx
    SigningCertPasswordPath = $signingCertPasswordPath
    SigningCertArgs         = $signingCertArgs
    HasSigningCert          = (-not [string]::IsNullOrEmpty($signingCertPfx))
}
