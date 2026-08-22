$dest      = Join-Path $env:LOCALAPPDATA "Serpy\runtime\qemu-11.1.0"
$installer = Join-Path $env:TEMP         "qemu-w64-setup.exe"

[void][System.IO.Directory]::CreateDirectory((Split-Path $dest -Parent))

Write-Host "Dest:      $dest"
Write-Host "Installer: $installer ($([System.IO.FileInfo]$installer | % Length) bytes)"
Write-Host "Running NSIS silent install..."

$p = Start-Process -FilePath $installer -ArgumentList "/S /D=$dest" -Wait -PassThru
Write-Host "Exit code: $($p.ExitCode)"

$exe = Join-Path $dest "qemu-system-x86_64.exe"
if (Test-Path $exe) {
    Write-Host "=== qemu-system-x86_64 found ==="
    & $exe --version
    Write-Host ""
    Write-Host "=== WHPX probe ==="
    & $exe -accel whpx -machine none -L (Join-Path $dest "share\qemu") 2>&1
} else {
    Write-Host "ERROR: qemu-system-x86_64.exe not found after install"
    Get-ChildItem $dest -ErrorAction SilentlyContinue | Select-Object Name | Format-Table
}
