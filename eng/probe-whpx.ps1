$dest = Join-Path $env:LOCALAPPDATA "Serpy\runtime\qemu-11.1.0"
$exe  = Join-Path $dest "qemu-system-x86_64.exe"
$lib  = Join-Path $dest "share\qemu"

Write-Host "=== -version ==="
& $exe --version

Write-Host ""
Write-Host "=== TLS creds-x509 probe (needs GnuTLS) ==="
$certDir = Join-Path $env:TEMP "serpy-tls-probe"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null
# We can't generate certs here easily, just test that the object type is recognised
$p = Start-Process -FilePath $exe `
    -ArgumentList "-machine none -object tls-creds-x509,id=t0,endpoint=server,verify-peer=yes,dir=$certDir -nographic" `
    -Wait -PassThru -NoNewWindow -RedirectStandardError "$certDir\stderr.txt"
$stderr = Get-Content "$certDir\stderr.txt" -ErrorAction SilentlyContinue
if ($stderr -match "Object type not found") {
    Write-Host "FAIL: GnuTLS not available in this build"
} elseif ($stderr -match "no cert file") {
    Write-Host "PASS: tls-creds-x509 object type recognised (cert dir empty, expected error)"
} else {
    Write-Host "Output: $stderr"
}

Write-Host ""
Write-Host "=== WHPX probe (-accel whpx -machine q35) ==="
$p2 = Start-Process -FilePath $exe `
    -ArgumentList "-accel whpx -machine q35 -L $lib -nographic -S" `
    -PassThru -NoNewWindow -RedirectStandardError "$certDir\whpx-err.txt"
Start-Sleep -Seconds 3
$whpxErr = Get-Content "$certDir\whpx-err.txt" -ErrorAction SilentlyContinue
$p2 | Stop-Process -ErrorAction SilentlyContinue

if ($whpxErr -match "WHPX.*not present|WHPX.*unavailable|WHPX.*failed") {
    Write-Host "FAIL: WHPX not available - enable HypervisorPlatform and reboot"
    Write-Host $whpxErr
} elseif ($whpxErr -match "whpx") {
    Write-Host "WHPX output: $whpxErr"
} else {
    Write-Host "PASS: QEMU started with WHPX (no WHPX error in stderr)"
    Write-Host "stderr: $whpxErr"
}

Remove-Item -Recurse -Force $certDir -ErrorAction SilentlyContinue
