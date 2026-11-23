#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Resolves and validates a git tag for release packaging into a Windows
    Installer ProductVersion (major.minor.build) and release metadata.

.DESCRIPTION
    Extracts a 3-part Windows Installer version (major.minor.build) from a git
    tag, validates the Windows Installer bounds (major <= 255, minor <= 255,
    build <= 65535), detects prerelease tags, and emits step outputs to
    $env:GITHUB_OUTPUT when running inside GitHub Actions. Also outputs a
    PSCustomObject for local and diagnostic invocation.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Tag = $(
        if ($env:GITHUB_REF_NAME) { $env:GITHUB_REF_NAME }
        elseif ($env:GITHUB_REF) { $env:GITHUB_REF }
        else { $null }
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "No git tag provided. Pass -Tag <version> or set GITHUB_REF_NAME / GITHUB_REF."
}

# Match [refs/tags/]v?MAJOR.MINOR.BUILD[-PRERELEASE]
$pattern = '^(?:refs/tags/)?v?([0-9]+)\.([0-9]+)\.([0-9]+)(?:-([0-9A-Za-z.-]+))?$'
if ($Tag -notmatch $pattern) {
    throw "Tag '$Tag' is malformed. Expected format: [refs/tags/]v?MAJOR.MINOR.BUILD[-PRERELEASE] (e.g. 'v1.0.0', 'v1.2.3-rc.1')."
}

$rawMajor = $Matches[1]
$rawMinor = $Matches[2]
$rawBuild = $Matches[3]
$prerelease = if ($Matches.Count -ge 5 -and $Matches[4]) { $Matches[4] } else { $null }

# Parse safely to prevent unhandled 64-bit integer overflow exceptions
[long]$major = 0
[long]$minor = 0
[long]$build = 0

if (-not [long]::TryParse($rawMajor, [ref]$major) -or $major -lt 0 -or $major -gt 255) {
    throw "Major version '$rawMajor' out of range for Windows Installer ProductVersion (must be 0..255)."
}
if (-not [long]::TryParse($rawMinor, [ref]$minor) -or $minor -lt 0 -or $minor -gt 255) {
    throw "Minor version '$rawMinor' out of range for Windows Installer ProductVersion (must be 0..255)."
}
if (-not [long]::TryParse($rawBuild, [ref]$build) -or $build -lt 0 -or $build -gt 65535) {
    throw "Build version '$rawBuild' out of range for Windows Installer ProductVersion (must be 0..65535)."
}

$productVersion = "$major.$minor.$build"
$isPrerelease = -not [string]::IsNullOrEmpty($prerelease)
$msiFilename = "Serpy-$productVersion.msi"

# Normalize release_tag: if Tag started with refs/tags/, strip that prefix; otherwise keep Tag as passed
$releaseTag = $Tag -replace '^refs/tags/', ''

if ($env:GITHUB_OUTPUT) {
    @(
        "product_version=$productVersion"
        "release_tag=$releaseTag"
        "is_prerelease=$($isPrerelease.ToString().ToLowerInvariant())"
        "msi_filename=$msiFilename"
    ) | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

[PSCustomObject]@{
    ProductVersion = $productVersion
    ReleaseTag     = $releaseTag
    IsPrerelease   = $isPrerelease
    MsiFilename    = $msiFilename
    Major          = [int]$major
    Minor          = [int]$minor
    Build          = [int]$build
    Prerelease     = $prerelease
}
