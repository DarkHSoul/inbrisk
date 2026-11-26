<#
.SYNOPSIS
    Creates a local self-signed Authenticode Code Signing certificate for Inbrisk development.

.DESCRIPTION
    Generates a self-signed Code Signing certificate in Cert:\CurrentUser\My using
    New-SelfSignedCertificate -Type CodeSigningCert and exports the public .cer file.

    When -InstallTrust is specified (requires an elevated Administrator PowerShell prompt),
    the exported .cer is also imported into LocalMachine\Root and LocalMachine\TrustedPublisher,
    which Windows requires to launch uiAccess="true" executables from C:\Program Files\Inbrisk.

.EXAMPLE
    .\tools\New-DevCodeSignCert.ps1
    Creates a dev signing cert and exports artifacts\certs\inbrisk-dev-codesign.cer.

.EXAMPLE
    .\tools\New-DevCodeSignCert.ps1 -InstallTrust
    (Run as Administrator) Creates the cert and trusts it in LocalMachine\Root and TrustedPublisher.
#>
[CmdletBinding()]
param(
    [string]$Subject = "CN=Inbrisk Local Dev Code Signing",
    [string]$FriendlyName = "Inbrisk Dev Code Signing",
    [int]$ValidYears = 3,
    [string]$OutDir = "",
    [string]$CerFileName = "inbrisk-dev-codesign.cer",
    [switch]$InstallTrust,
    [switch]$ExportPfx,
    [SecureString]$PfxPassword
)

$ErrorActionPreference = "Stop"

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($InstallTrust -and -not (Test-IsElevated)) {
    throw "-InstallTrust requires an elevated (Run as Administrator) PowerShell prompt to import into LocalMachine\Root and LocalMachine\TrustedPublisher."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot "artifacts\certs"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$notAfter = (Get-Date).AddYears($ValidYears)
Write-Host "Creating self-signed Code Signing certificate ($Subject, valid until $($notAfter.ToString('yyyy-MM-dd')))..."

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -FriendlyName $FriendlyName `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter $notAfter

$cerPath = Join-Path $OutDir $CerFileName
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host "Exported public certificate: $cerPath"

$pfxPath = $null
if ($ExportPfx) {
    if (-not $PfxPassword) {
        $PfxPassword = Read-Host -AsSecureString "Enter password for exported PFX"
    }
    $pfxPath = Join-Path $OutDir ([System.IO.Path]::ChangeExtension($CerFileName, ".pfx"))
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $PfxPassword -Force | Out-Null
    Write-Host "Exported PFX certificate:    $pfxPath"
}

if ($InstallTrust) {
    Write-Host "Installing certificate into Cert:\LocalMachine\Root..."
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

    Write-Host "Installing certificate into Cert:\LocalMachine\TrustedPublisher..."
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null

    Write-Host "Trust installed. Windows will now trust binaries signed with this dev certificate (including uiAccess='true' under Program Files)."
}

Write-Host ""
Write-Host "Certificate created successfully:"
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  NotAfter:   $($cert.NotAfter.ToString('u'))"
Write-Host "  CER Path:   $cerPath"
if ($pfxPath) {
    Write-Host "  PFX Path:   $pfxPath"
}
Write-Host ""
Write-Host "To sign release builds with this certificate:"
Write-Host "  `$env:INBRISK_SIGN_SHA1 = `"$($cert.Thumbprint)`""
Write-Host "  .\tools\publish.ps1 -Sign"
if (-not $InstallTrust) {
    Write-Host ""
    Write-Host "Tip: To trust this cert locally (required for uiAccess='true' testing), re-run as Administrator with -InstallTrust,"
    Write-Host "or import '$cerPath' into LocalMachine\Root and LocalMachine\TrustedPublisher."
}

[PSCustomObject]@{
    Thumbprint   = $cert.Thumbprint
    Subject      = $cert.Subject
    NotAfter     = $cert.NotAfter
    CerPath      = $cerPath
    PfxPath      = $pfxPath
    TrustInstalled = $InstallTrust.IsPresent
}
