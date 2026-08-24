#!/usr/bin/env pwsh
<#
.SYNOPSIS
    CI-only: Build the Serpy MSI installer (IU3/IR5/IKD3) from Native-AOT
    publish output. Never invoked during application operation.

.DESCRIPTION
    Harvests Serpy.App's publish directory (which already contains
    config/versions.yaml, cloud-init/*, guest/* per Serpy.App.csproj's own
    CopyToOutputDirectory items -- the same payload eng/package-windows.ps1
    validates as a complete distribution) and Serpy.InstallerBootstrapper's
    publish directory (the separate signed elevated helper, IR3/IKTD1) into
    one per-machine MSI installed to %ProgramFiles%\Serpy.

    -DescriptorPath is REQUIRED for a normal build: a real, released MSI
    whose bootstrapper cannot verify a runtime descriptor is guaranteed to
    fail closed on first-run QEMU setup (IU3's whole point is a working
    clean-host install flow). Pass -AllowMissingDescriptor to explicitly
    opt out for local/dev/test packaging convenience only -- this switch
    must never be used for a build that will actually be distributed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPublishDir,
    [Parameter(Mandatory = $true)]
    [string]$BootstrapperPublishDir,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$Version = "0.0.0",
    [string]$DescriptorPath,
    [switch]$AllowMissingDescriptor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wxsPath = Join-Path $PSScriptRoot "Serpy.wxs"

$appPublish = (Resolve-Path -LiteralPath $AppPublishDir).Path
$bootstrapperPublish = (Resolve-Path -LiteralPath $BootstrapperPublishDir).Path

# Same required-payload list eng/package-windows.ps1 enforces for the ZIP --
# this MSI's harvest is not considered complete unless the app publish dir
# passes the identical check.
$requiredAppFiles = @(
    'Serpy.App.exe',
    'config\versions.yaml',
    'cloud-init\user-data',
    'cloud-init\meta-data',
    'guest\init-data.sh',
    'guest\recover.sh',
    'guest\provision-done.sh'
)
$missing = $requiredAppFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $appPublish $_)) }
if ($missing) {
    throw "App publish directory is not a complete Native-AOT Serpy distribution: $($missing -join ', ')"
}

$bootstrapperExe = Join-Path $bootstrapperPublish 'Serpy.InstallerBootstrapper.exe'
if (-not (Test-Path -LiteralPath $bootstrapperExe)) {
    throw "Bootstrapper publish directory is missing Serpy.InstallerBootstrapper.exe: $bootstrapperExe"
}

if (-not $DescriptorPath -and -not $AllowMissingDescriptor) {
    throw "DescriptorPath is required for a distributable MSI (the installed bootstrapper " +
        "cannot verify installer authenticity without it -- IU3/IR3/IKTD3). Pass " +
        "-AllowMissingDescriptor explicitly for local/dev/test packaging only."
}
if ($DescriptorPath) {
    if (-not (Test-Path -LiteralPath $DescriptorPath)) {
        throw "DescriptorPath does not exist: $DescriptorPath"
    }
    $envelope = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
    $required = @('descriptor', 'signatureBase64')
    $missingFields = $required | Where-Object { -not $envelope.PSObject.Properties[$_] -or -not $envelope.$_ }
    if ($missingFields) {
        throw "DescriptorPath does not look like a signed descriptor envelope (missing: $($missingFields -join ', ')): $DescriptorPath"
    }
}

$notice = Join-Path $repoRoot 'docs\third-party\qemu-source-notice.md'
if (-not (Test-Path -LiteralPath $notice)) {
    throw "Missing required QEMU source notice: $notice"
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "The 'wix' CLI is not on PATH. Install with: dotnet tool install --global wix --version 5.0.2"
}

# WiX v5 specifically (not v7+, which requires accepting a paid Open Source
# Maintenance Fee EULA -- inappropriate for an unattended CI build tool).
$wixVersion = (wix --version 2>&1 | Select-Object -First 1)
if ($wixVersion -notmatch '^5\.') {
    throw "Expected WiX v5.x, got '$wixVersion'. Install with: dotnet tool install --global wix --version 5.0.2"
}

$extensions = wix extension list 2>&1
if ($extensions -notmatch 'WixToolset\.Util\.wixext') {
    wix extension add WixToolset.Util.wixext/5.0.2 | Out-Null
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

$buildArgs = @(
    'build', $wxsPath,
    '-arch', 'x64',
    '-ext', 'WixToolset.Util.wixext',
    '-d', "AppPublishDir=$appPublish",
    '-d', "BootstrapperPublishDir=$bootstrapperPublish",
    '-d', "RepoRoot=$repoRoot",
    '-d', "ProductVersion=$Version",
    '-out', $OutputPath
)
if ($DescriptorPath) {
    $buildArgs += @('-d', "DescriptorPath=$((Resolve-Path -LiteralPath $DescriptorPath).Path)")
}

& wix @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE."
}

Write-Host "Created Serpy MSI: $OutputPath"
