[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\.local-signing"),
    [string]$Subject = "CN=Serpy Test Publisher, O=Serpy Dev, C=US",
    [SecureString]$Password = (ConvertTo-SecureString -String "serpy-test-only" -Force -AsPlainText)
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

# Clean up the CurrentUser\My store entry -- the .pfx file is the durable artifact.
Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
