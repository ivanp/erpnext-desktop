#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$Version = "dev"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$required = @(
    'Serpy.App.exe',
    'config\versions.yaml',
    'cloud-init\user-data',
    'cloud-init\meta-data',
    'guest\init-data.sh',
    'guest\recover.sh',
    'guest\provision-done.sh'
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publish $_)) }
if ($missing) {
    throw "Publish directory is not a complete Native-AOT Serpy distribution: $($missing -join ', ')"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$notice = Join-Path $repoRoot 'docs\third-party\qemu-source-notice.md'
if (-not (Test-Path -LiteralPath $notice)) {
    throw "Missing required QEMU source notice: $notice"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$destination = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path $destination "Serpy-$Version-win-x64"
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $stage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'docs\third-party') | Out-Null
Copy-Item -LiteralPath $notice -Destination (Join-Path $stage 'docs\third-party\qemu-source-notice.md') -Force

$manifest = Get-ChildItem -LiteralPath $stage -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length).TrimStart('\') -replace '\\', '/'
        "$(($_ | Get-FileHash -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    }
$manifest | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ascii

$zip = "$stage.zip"
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Created portable Serpy package: $zip"
