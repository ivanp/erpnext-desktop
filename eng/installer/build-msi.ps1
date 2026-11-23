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

    The MSI also carries the official Microsoft .NET 10 runtime FILES
    (Serpy.InstallerBootstrapper.exe is published framework-dependent, so a
    clean Windows otherwise cannot run it): dotnet-runtime-<version>-win-x64.zip
    is fetched at BUILD time, verified against Microsoft's published SHA-512,
    extracted, and harvested into the MSI under C:\Program Files\dotnet\ as a
    conditional, PERMANENT feature installed only when no .NET runtime is
    detected (KD_MSI1). No child msiexec/Burn bundle runs during install (the
    nested runtime bundle cannot grab the Windows Installer mutex while
    Serpy.msi holds it: VM-verified failure 1618 -> 1603 rollback). The pinned
    version + SHA-512 live in this script's defaults below (-DotNetRuntimeZipPath
    overrides the download; CI passes the pre-downloaded zip).
    -AllowMissingDotNetRuntime builds without the runtime payload
    (dev/testing only, never for distribution; the installed MSI then honestly
    cannot make the helper runnable on a clean machine).
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
    [string]$DotNetRuntimeZipPath,
    [string]$DotNetRuntimeVersion = "10.0.15",
    [string]$DotNetRuntimeZipSha512 = "06f0bedd59ae4ac722ada805a6d6bbf605bcdc790c71f59b7003878072902b5cde5dec8c40c0baa81192660d4748e9d79afdbdcb0c0ea9d6b821d0a044c4c6dd",
    [switch]$AllowMissingDotNetRuntime,
    [switch]$AllowMissingDescriptor,

    # Authenticode signing options
    [string]$SigningCertPfx,
    [SecureString]$SigningCertPassword,
    [string]$SigningCertPasswordPath,
    [string]$SigningCertThumbprint,
    [switch]$SignWithLocalDevCert,
    [switch]$SignMsi
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
    'config\build-inputs.json',
    'cloud-init\user-data',
    'cloud-init\meta-data',
    'guest\init-data.sh',
    'guest\recover.sh',
    'guest\provision.sh'
)
$missing = $requiredAppFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $appPublish $_)) }
if ($missing) {
    throw "App publish directory is not a complete Native-AOT Serpy distribution: $($missing -join ', ')"
}

$bootstrapperExe = Join-Path $bootstrapperPublish 'Serpy.InstallerBootstrapper.exe'
if (-not (Test-Path -LiteralPath $bootstrapperExe)) {
    throw "Bootstrapper publish directory is missing Serpy.InstallerBootstrapper.exe: $bootstrapperExe"
}

if ($SignWithLocalDevCert) {
    $localSigningDir = Join-Path $repoRoot ".local-signing"
    $localPfx = Join-Path $localSigningDir "serpy-test-signing.pfx"
    $localPwdFile = Join-Path $localSigningDir "serpy-test-signing.pfx.password"
    if (-not (Test-Path -LiteralPath $localPfx)) {
        throw "Local dev signing certificate not found: $localPfx`nGenerate one using: ./eng/gen-test-signing-cert.ps1"
    }
    $SigningCertPfx = $localPfx
    if (-not $SigningCertPassword -and -not $SigningCertPasswordPath -and (Test-Path -LiteralPath $localPwdFile)) {
        $SigningCertPasswordPath = $localPwdFile
    }
}

if ($SigningCertPasswordPath -and -not $SigningCertPassword) {
    if (-not (Test-Path -LiteralPath $SigningCertPasswordPath)) {
        throw "Signing certificate password file not found: $SigningCertPasswordPath"
    }
    $plainPwd = (Get-Content -LiteralPath $SigningCertPasswordPath -Raw).Trim()
    $SigningCertPassword = ConvertTo-SecureString -String $plainPwd -Force -AsPlainText
}

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kitsRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    )
    foreach ($root in $kitsRoots) {
        if (Test-Path $root) {
            $signtools = Get-ChildItem -Path $root -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
                Sort-Object FullName -Descending
            if ($signtools) {
                return $signtools[0].FullName
            }
        }
    }
    return $null
}

function Invoke-AuthenticodeSign {
    param(
        [string]$FilePath,
        [string]$PfxPath,
        [SecureString]$Password,
        [string]$Thumbprint,
        [string]$SignToolPath
    )

    $plainPassword = if ($Password) { [System.Net.NetworkCredential]::new('', $Password).Password } else { $null }

    if ($SignToolPath) {
        $signArgs = @('sign', '/fd', 'SHA256')
        if ($PfxPath) {
            $signArgs += @('/f', (Resolve-Path -LiteralPath $PfxPath).Path)
            if ($plainPassword) {
                $signArgs += @('/p', $plainPassword)
            }
        } elseif ($Thumbprint) {
            $signArgs += @('/sha1', $Thumbprint)
        }
        $signArgs += (Resolve-Path -LiteralPath $FilePath).Path

        & $SignToolPath @signArgs
        if ($LASTEXITCODE -ne 0) {
            throw "signtool.exe failed with exit code $LASTEXITCODE for $FilePath."
        }
    } else {
        $cert = if ($PfxPath) {
            if ($Password) {
                New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
                    $PfxPath, $Password, 'Exportable')
            } else {
                New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
                    $PfxPath, [SecureString]::new(), 'Exportable')
            }
        } elseif ($Thumbprint) {
            Get-Item "Cert:\CurrentUser\My\$Thumbprint", "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction SilentlyContinue |
                Select-Object -First 1
        } else {
            $null
        }

        if (-not $cert) {
            throw "Could not load certificate for signing $FilePath."
        }

        $res = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $cert -HashAlgorithm SHA256
        $sig = Get-AuthenticodeSignature $FilePath
        if (-not $sig.SignerCertificate) {
            throw "Set-AuthenticodeSignature failed for $($FilePath): $($res.Status) $($res.StatusMessage)"
        }
    }
    Write-Host "Authenticode signed: $FilePath"
}

$shouldSign = [bool]($SigningCertPfx -or $SigningCertThumbprint)
$signTool = if ($shouldSign) { Find-SignTool } else { $null }
if ($shouldSign) {
    if ($signTool) {
        Write-Host "Using signtool: $signTool"
    } else {
        Write-Host "signtool.exe not found; falling back to Set-AuthenticodeSignature."
    }

    Invoke-AuthenticodeSign `
        -FilePath $bootstrapperExe `
        -PfxPath $SigningCertPfx `
        -Password $SigningCertPassword `
        -Thumbprint $SigningCertThumbprint `
        -SignToolPath $signTool
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

# ── Harvested .NET runtime files (KD_MSI1) ──────────────────────────────────
# The elevated Serpy.InstallerBootstrapper.exe helper is framework-dependent
# (--self-contained false), so a clean Windows must get a .NET runtime from
# the MSI itself. The official dotnet-runtime win-x64 .exe is a Burn bundle
# whose nested msiexec cannot run while Serpy.msi holds the Windows Installer
# execution mutex (VM-verified: "Failed to grab execution mutex" -> 1618 ->
# WixQuietExec64 error -> 1603 rollback), so NO installer is executed at
# install time. Instead the official ZIP's files are harvested directly into
# the MSI under C:\Program Files\dotnet\ (the default machine-wide location
# hostfxr resolves), as a conditional PERMANENT feature installed only when
# no runtime is present. One pin: DotNetRuntimeVersion + DotNetRuntimeZipSha512
# (defaults above), verified at build time against Microsoft's published
# SHA-512 (releases.json and the per-release checksums file both publish
# SHA-512 for the zip).
$dotNetRuntimeDir = $null
$dotNetHarvestWxs = $null
if (-not $AllowMissingDotNetRuntime) {
    $dotNetZipDir = Join-Path $repoRoot ".tmp-vm\dotnet-runtime-zip"
    if ($DotNetRuntimeZipPath) {
        if (-not (Test-Path -LiteralPath $DotNetRuntimeZipPath)) {
            throw "DotNetRuntimeZipPath does not exist: $DotNetRuntimeZipPath"
        }
        $dotNetZipPath = (Resolve-Path -LiteralPath $DotNetRuntimeZipPath).Path
    } else {
        # Build-time cache: downloaded once per (version, hash) and never
        # committed to git (.tmp-vm/ is gitignored; CI fetches it explicitly).
        $dotNetZipPath = Join-Path $dotNetZipDir "dotnet-runtime-$DotNetRuntimeVersion-win-x64.zip"
        if (-not (Test-Path -LiteralPath $dotNetZipPath)) {
            $dotNetUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/$DotNetRuntimeVersion/dotnet-runtime-$DotNetRuntimeVersion-win-x64.zip"
            Write-Host "Downloading .NET runtime zip: $dotNetUrl"
            New-Item -ItemType Directory -Force -Path $dotNetZipDir | Out-Null
            Invoke-WebRequest -Uri $dotNetUrl -OutFile $dotNetZipPath
        }
    }

    $actualSha512 = (Get-FileHash -LiteralPath $dotNetZipPath -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actualSha512 -ne $DotNetRuntimeZipSha512.ToLowerInvariant()) {
        throw ("Bundled .NET runtime zip hash mismatch for {0}:`n  expected SHA-512 {1}`n  actual   SHA-512 {2}`n" +
            "Update DotNetRuntimeVersion/DotNetRuntimeZipSha512 together from " +
            "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json " +
            "or the per-release checksums file") -f `
            $dotNetZipPath, $DotNetRuntimeZipSha512, $actualSha512
    }

    # Extract into a stable staging dir the wxs harvests via -d DotNetRuntimeDir=.
    # Extraction is skipped when the previous extract already matches this zip
    # (same-length marker file holding the zip hash) to keep CI incremental.
    $dotNetRuntimeDir = Join-Path $dotNetZipDir "extracted"
    $extractMarker = Join-Path $dotNetZipDir "extracted.sha512"
    $needsExtract = -not (Test-Path -LiteralPath $dotNetRuntimeDir) -or
        (-not (Test-Path -LiteralPath $extractMarker)) -or
        ((Get-Content -LiteralPath $extractMarker -Raw).Trim() -ne $actualSha512)
    if ($needsExtract) {
        Write-Host "Extracting .NET runtime files to: $dotNetRuntimeDir"
        if (Test-Path -LiteralPath $dotNetRuntimeDir) {
            Remove-Item -Recurse -Force -LiteralPath $dotNetRuntimeDir
        }
        Expand-Archive -LiteralPath $dotNetZipPath -DestinationPath $dotNetRuntimeDir -Force
        Set-Content -LiteralPath $extractMarker -Value $actualSha512 -NoNewline
    }
    foreach ($required in @('dotnet.exe', 'host\fxr', 'shared\Microsoft.NETCore.App', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dotNetRuntimeDir $required))) {
            throw "Extracted runtime zip is missing expected entry '$required' in $dotNetRuntimeDir"
        }
    }
    # Generate a companion wxs fragment harvesting every file into its own
    # permanent 64-bit component under [ProgramFiles64Folder]dotnet\.
    # Explicit components guarantee Permanent="yes" (bit 0x10) on every
    # harvested file so uninstalling Serpy leaves the shared runtime intact.
    $dotNetHarvestWxs = Join-Path $dotNetZipDir "harvest.wxs"
    $harvestFiles = Get-ChildItem -LiteralPath $dotNetRuntimeDir -Recurse -File
    $harvestDirs = Get-ChildItem -LiteralPath $dotNetRuntimeDir -Recurse -Directory | Sort-Object FullName
    $dirMap = @{ "" = "DotNetRuntimeFolder" }
    $dIdx = 0
    foreach ($d in $harvestDirs) {
        $rel = $d.FullName.Substring($dotNetRuntimeDir.Length).TrimStart('\', '/')
        $dirMap[$rel] = "dnet_dir_$dIdx"
        $dIdx++
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <ComponentGroup Id="DotNetRuntimeFiles">')
    $cIdx = 0
    foreach ($f in $harvestFiles) {
        $relDir = ""
        if ($f.DirectoryName.Length -gt $dotNetRuntimeDir.Length) {
            $relDir = $f.DirectoryName.Substring($dotNetRuntimeDir.Length).TrimStart('\', '/')
        }
        $targetDirId = $dirMap[$relDir]
        $cId = "dnet_c_$cIdx"
        $fId = "dnet_f_$cIdx"
        [void]$sb.AppendLine("      <Component Id=`"$cId`" Directory=`"$targetDirId`" Permanent=`"yes`" Bitness=`"always64`">")
        [void]$sb.AppendLine("        <File Id=`"$fId`" Source=`"$($f.FullName)`" KeyPath=`"yes`" Checksum=`"yes`" />")
        [void]$sb.AppendLine("      </Component>")
        $cIdx++
    }
    [void]$sb.AppendLine('      <Component Id="DotNetRuntimeRegistryComponent" Directory="DotNetRuntimeFolder" Permanent="yes" Bitness="always64">')
    [void]$sb.AppendLine('        <RegistryValue Root="HKLM" Key="SOFTWARE\dotnet\Setup\InstalledVersions\x64" Name="InstallLocation" Value="[DotNetRuntimeFolder]" Type="string" KeyPath="yes" />')
    [void]$sb.AppendLine("        <RegistryValue Root=`"HKLM`" Key=`"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App\$DotNetRuntimeVersion`" Value=`"1`" Type=`"integer`" />")
    [void]$sb.AppendLine('      </Component>')
    [void]$sb.AppendLine('    </ComponentGroup>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
    [void]$sb.AppendLine('      <Directory Id="DotNetRuntimeFolder" Name="dotnet">')
    function Emit-SubDirs($parentDir, $parentRel, $sbRef, $dMapRef) {
        $subs = Get-ChildItem -LiteralPath $parentDir -Directory | Sort-Object Name
        foreach ($s in $subs) {
            $subRel = $s.FullName.Substring($dotNetRuntimeDir.Length).TrimStart('\', '/')
            $subId = $dMapRef[$subRel]
            [void]$sbRef.AppendLine("        <Directory Id=`"$subId`" Name=`"$($s.Name)`">")
            Emit-SubDirs $s.FullName $subRel $sbRef $dMapRef
            [void]$sbRef.AppendLine("        </Directory>")
        }
    }
    foreach ($d in $harvestDirs) {
        $rel = $d.FullName.Substring($dotNetRuntimeDir.Length).TrimStart('\', '/')
        if (-not $rel.Contains('\')) {
            $topId = $dirMap[$rel]
            [void]$sb.AppendLine("        <Directory Id=`"$topId`" Name=`"$($d.Name)`">")
            Emit-SubDirs $d.FullName $rel $sb $dirMap
            [void]$sb.AppendLine("        </Directory>")
        }
    }
    [void]$sb.AppendLine('      </Directory>')
    [void]$sb.AppendLine('    </StandardDirectory>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('</Wix>')
    Set-Content -LiteralPath $dotNetHarvestWxs -Value $sb.ToString() -Encoding utf8
    Write-Host "Generated runtime harvest wxs: $dotNetHarvestWxs ($cIdx files)"
    Write-Host "Bundled .NET runtime verified: dotnet-runtime-$DotNetRuntimeVersion-win-x64.zip (SHA-512 OK)"
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
if ($extensions -notmatch 'WixToolset\.UI\.wixext') {
    wix extension add WixToolset.UI.wixext/5.0.2 | Out-Null
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

$sources = @($wxsPath)
if ($dotNetHarvestWxs -and (Test-Path -LiteralPath $dotNetHarvestWxs)) {
    $sources += $dotNetHarvestWxs
}
$buildArgs = @('build') + $sources + @(
    '-arch', 'x64',
    '-ext', 'WixToolset.Util.wixext',
    '-ext', 'WixToolset.UI.wixext',
    '-d', "AppPublishDir=$appPublish",
    '-d', "BootstrapperPublishDir=$bootstrapperPublish",
    '-d', "RepoRoot=$repoRoot",
    '-d', "ProductVersion=$Version",
    '-out', $OutputPath
)
if ($DescriptorPath) {
    $buildArgs += @('-d', "DescriptorPath=$((Resolve-Path -LiteralPath $DescriptorPath).Path)")
}
if ($dotNetRuntimeDir) {
    $buildArgs += @('-d', "DotNetRuntimeDir=1")
}

& wix @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE."
}
Write-Host "Created Serpy MSI: $OutputPath"

if ($shouldSign -and ($SignMsi -or -not $PSBoundParameters.ContainsKey('SignMsi'))) {
    Invoke-AuthenticodeSign `
        -FilePath $OutputPath `
        -PfxPath $SigningCertPfx `
        -Password $SigningCertPassword `
        -Thumbprint $SigningCertThumbprint `
        -SignToolPath $signTool
}
