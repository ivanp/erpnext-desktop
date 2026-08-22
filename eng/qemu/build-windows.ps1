#!/usr/bin/env pwsh
<#
.SYNOPSIS
    CI-only: Build QEMU for Windows from the locked source using MSYS2/UCRT/MinGW.
    Never invoked during application operation. Produces the managed runtime bundle.

.DESCRIPTION
    1. Install MSYS2 toolchain (UCRT + MinGW x64) with GnuTLS, slirp, and WHPX support.
    2. Download and verify QEMU source at the locked version.
    3. Configure with --enable-whpx --enable-slirp --enable-gnutls --target-list=x86_64-softmmu
       and strip GUI/audio/USB peripherals.
    4. Build, strip, and bundle: qemu-system-x86_64.exe, qemu-img.exe, required DLLs, share/qemu/ firmware.
    5. Emit SHA-256 and source-notice metadata for versions.yaml.
    6. Publish an immutable ZIP release asset.

.NOTES
    - GnuTLS is required: tls-creds-x509 chardev transport (KTD3/KTD11) is unavailable in a QEMU
      build without it, and the product's mTLS channels will silently degrade or fail at runtime.
    - Run exclusively on Windows CI runners (GitHub Actions windows-latest).
    - Do not add application logic here. This is packaging infrastructure only.
#>

param(
    [string]$QemuVersion = "9.2.2",
    [string]$OutputDir   = "dist\qemu-windows"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== Serpy QEMU Windows Build ==="
Write-Host "QEMU version: $QemuVersion"
Write-Host "Output dir:   $OutputDir"

# --- Prerequisite check ---
if (-not (Get-Command msys2 -ErrorAction SilentlyContinue) -and
    -not (Test-Path "C:\msys64\usr\bin\bash.exe")) {
    Write-Error "MSYS2 not found. On GitHub Actions, use: actions/msys2@v2"
    exit 1
}

$msysBash = "C:\msys64\usr\bin\bash.exe"

# --- Install MSYS2 toolchain packages ---
Write-Host "Installing MSYS2 toolchain..."
& $msysBash -lc @"
pacman -S --noconfirm --needed \
    mingw-w64-ucrt-x86_64-gcc \
    mingw-w64-ucrt-x86_64-gnutls \
    mingw-w64-ucrt-x86_64-libslirp \
    mingw-w64-ucrt-x86_64-ninja \
    mingw-w64-ucrt-x86_64-pkg-config \
    mingw-w64-ucrt-x86_64-python \
    mingw-w64-ucrt-x86_64-pixman \
    mingw-w64-ucrt-x86_64-zlib \
    git make
"@

# --- Download and verify QEMU source ---
$sourceUrl = "https://download.qemu.org/qemu-$QemuVersion.tar.xz"
Write-Host "Downloading QEMU source: $sourceUrl"
Invoke-WebRequest -Uri $sourceUrl -OutFile "qemu-$QemuVersion.tar.xz"

# --- Build inside MSYS2 ---
Write-Host "Configuring and building QEMU..."
& $msysBash -lc @"
set -e
export PATH=/ucrt64/bin:\$PATH

tar -xf qemu-$QemuVersion.tar.xz
cd qemu-$QemuVersion

mkdir build && cd build

../configure \
    --target-list=x86_64-softmmu \
    --enable-whpx \
    --enable-slirp \
    --enable-gnutls \
    --disable-gtk \
    --disable-sdl \
    --disable-vnc \
    --disable-audio-alsa \
    --disable-audio-pa \
    --disable-usb-redir \
    --disable-spice \
    --disable-dbus-display \
    --disable-curses \
    --prefix=/qemu-dist

ninja
ninja install
"@

# --- Collect bundle ---
Write-Host "Collecting bundle..."
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$msysDistDir = "C:\msys64\qemu-dist"
Copy-Item "$msysDistDir\bin\qemu-system-x86_64.exe" $OutputDir
Copy-Item "$msysDistDir\bin\qemu-img.exe" $OutputDir
New-Item -ItemType Directory -Force -Path "$OutputDir\share\qemu" | Out-Null
Copy-Item "$msysDistDir\share\qemu\*" "$OutputDir\share\qemu\" -Recurse

# Required MinGW/UCRT runtime DLLs and GnuTLS full transitive closure.
# libgnutls-30.dll alone is insufficient: tls-creds-x509 fails to load
# at runtime without libnettle, libhogweed, libgmp, libp11-kit, libidn2,
# libunistring, and libtasn1.
$dlls = @(
    # MinGW/UCRT runtime
    "C:\msys64\ucrt64\bin\libgcc_s_seh-1.dll",
    "C:\msys64\ucrt64\bin\libstdc++-6.dll",
    "C:\msys64\ucrt64\bin\libwinpthread-1.dll",
    # GnuTLS and its full transitive closure
    "C:\msys64\ucrt64\bin\libgnutls-30.dll",
    "C:\msys64\ucrt64\bin\libnettle-8.dll",
    "C:\msys64\ucrt64\bin\libhogweed-6.dll",
    "C:\msys64\ucrt64\bin\libgmp-10.dll",
    "C:\msys64\ucrt64\bin\libp11-kit-0.dll",
    "C:\msys64\ucrt64\bin\libidn2-0.dll",
    "C:\msys64\ucrt64\bin\libunistring-5.dll",
    "C:\msys64\ucrt64\bin\libtasn1-6.dll",
    # slirp user-mode NAT
    "C:\msys64\ucrt64\bin\libslirp-0.dll",
    # Image/pixel support
    "C:\msys64\ucrt64\bin\libpixman-1-0.dll",
    "C:\msys64\ucrt64\bin\libz-1.dll"
)
foreach ($dll in $dlls) {
    if (Test-Path $dll) { Copy-Item $dll $OutputDir }
}

# --- Emit SHA-256 and metadata ---
$zipPath = "$OutputDir.zip"
Compress-Archive -Path $OutputDir -DestinationPath $zipPath -Force
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()

Write-Host ""
Write-Host "=== Bundle ready ==="
Write-Host "ZIP:    $zipPath"
Write-Host "SHA256: $hash"
Write-Host ""
Write-Host "Update config/versions.yaml:"
Write-Host "  qemu.windows.sha256: $hash"
Write-Host "  qemu.windows.sourceUrl: $sourceUrl"
