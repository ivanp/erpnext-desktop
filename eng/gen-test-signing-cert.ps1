[CmdletBinding()]
param(
    [string]$OutDir,
    [string]$Subject = "CN=Serpy Test Publisher, O=Serpy Dev, C=US",
    [SecureString]$Password
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty when evaluated as a default parameter value in some
# invocation styles (e.g. via -File with certain hosts); resolve it in the body
# instead, where it is reliably populated.
if ([string]::IsNullOrEmpty($OutDir)) {
    $OutDir = Join-Path $PSScriptRoot "..\.local-signing"
}

# Never hardcode a PFX password in a tracked script. Generate a fresh
# high-entropy one for this run when the caller supplies none.
if (-not $Password) {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $plainPassword = [Convert]::ToBase64String($bytes)
    $Password = ConvertTo-SecureString -String $plainPassword -Force -AsPlainText
} else {
    $plainPassword = [System.Net.NetworkCredential]::new('', $Password).Password
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -FriendlyName "Serpy Test Signing Cert (NOT FOR PRODUCTION)" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(2)

$pfxPath = Join-Path $OutDir "serpy-test-signing.pfx"
$cerPath = Join-Path $OutDir "serpy-test-signing.cer"

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

Write-Output "Thumbprint=$($cert.Thumbprint)"
Write-Output "Subject=$($cert.Subject)"
Write-Output "PfxPath=$pfxPath"
Write-Output "CerPath=$cerPath"
# Written to a gitignored sibling file (never the repo, never a log) so the
# opt-in integration test that consumes this throwaway cert can read it
# without a human re-entering it every run. Always written -- whether the
# password was generated here or supplied by the caller -- so the test's
# "cert + password file both present" precondition never silently fails to
# hold. Never do this for a real signing key: acceptable only because this
# cert is a disposable local test fixture, not production signing material.
$passwordPath = "$pfxPath.password"
Set-Content -Path $passwordPath -Value $plainPassword -NoNewline
Write-Output "PfxPasswordPath=$passwordPath"

# Clean up the CurrentUser\My store entry -- the .pfx file is the durable artifact.
Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
