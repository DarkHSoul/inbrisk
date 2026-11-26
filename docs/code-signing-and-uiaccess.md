# Authenticode Code Signing & `uiAccess="true"` Guide

Inbrisk supports optional Authenticode code signing and an opt-in `uiAccess="true"` application manifest so normal developer/per-user builds remain frictionless while production and elevated-UI builds meet Windows security requirements.

## 1. Local Development Workflow (Self-Signed Certificate)

Create a local self-signed Code Signing certificate in `Cert:\CurrentUser\My`:

```powershell
# Basic dev cert (exports artifacts\certs\inbrisk-dev-codesign.cer)
.\tools\New-DevCodeSignCert.ps1

# Elevated PowerShell: also installs trust into LocalMachine\Root and LocalMachine\TrustedPublisher
# (required for local uiAccess="true" testing)
.\tools\New-DevCodeSignCert.ps1 -InstallTrust
```

Sign a single artifact directly:

```powershell
$env:INBRISK_SIGN_SHA1 = "<THUMBPRINT>"
.\tools\Sign-Artifact.ps1 -Path artifacts\publish\inbrisk.exe
```

Or run a full signed release build:

```powershell
$env:INBRISK_SIGN_SHA1 = "<THUMBPRINT>"
.\tools\publish.ps1 -Version 1.0.0 -Sign
```

## 2. Dual Manifest & `uiAccess="true"` Requirements

Inbrisk ships two application manifests in `src/Inbrisk.Cli/`:

| Manifest | MSBuild Property | Default | Purpose |
|---|---|---|---|
| `app.manifest` | `InbriskUiAccess=false` | Yes | Normal dev and per-user installs (`uiAccess="false"`). Works unsigned. |
| `app.uiaccess.manifest` | `-p:InbriskUiAccess=true` | No | Opt-in UI Automation / elevated window access (`uiAccess="true"`). |

Windows strictly enforces two requirements before launching any executable with `uiAccess="true"`:

1. **Valid, trusted Authenticode signature**: The binary must be signed with a certificate chaining to `LocalMachine\Root` (and trusted in `LocalMachine\TrustedPublisher` for self-signed dev certs).
2. **Secure installation directory**: The binary must reside in a write-protected system location such as `C:\Program Files\Inbrisk` (not `%LOCALAPPDATA%` or `bin\Debug`).

Build and package a signed `uiAccess="true"` release:

```powershell
$env:INBRISK_SIGN_SHA1 = "<THUMBPRINT>"
.\tools\publish.ps1 -Version 1.0.0 -Sign -UiAccess
```

When `-UiAccess` is passed to `tools/publish.ps1`:
- `dotnet publish` receives `-p:InbriskUiAccess=true` (embedding `app.uiaccess.manifest`).
- `inbrisk.exe` is signed via `tools/Sign-Artifact.ps1` before packaging.
- `installer/inbrisk.iss` receives `/DUiAccess` and `/DSignCert`, requiring `PrivilegesRequired=admin` (installing to `C:\Program Files\Inbrisk`) and signing both `InbriskSetup.exe` and its embedded uninstaller.

## 3. Production Signing (Azure Trusted Signing & EV/OV Certificates)

`tools/Sign-Artifact.ps1` auto-discovers `signtool.exe` from the Windows 10/11 SDK, signs with SHA-256 and RFC 3161 timestamping, and verifies every artifact with `signtool verify /pa /v`.

### Option A: Azure Trusted Signing (Recommended for CI/CD)

1. Provision an Azure Trusted Signing account and certificate profile.
2. Install `Microsoft.Trusted.Signing.Client` (provides `Azure.CodeSigning.Dlib.dll`).
3. Create a metadata JSON file (e.g. `trusted-signing-metadata.json`):

```json
{
  "Endpoint": "https://eus.codesigning.azure.net/",
  "CodeSigningAccountName": "inbrisk-signing",
  "CertificateProfileName": "inbrisk-public-trust"
}
```

4. Configure environment variables in CI and publish:

```powershell
$env:INBRISK_AZURE_SIGNING_METADATA = "C:\ci\trusted-signing-metadata.json"
# Optional if not in default NuGet/LocalAppData path:
$env:INBRISK_AZURE_SIGNING_DLIB = "C:\tools\Azure.CodeSigning.Dlib.dll"
.\tools\publish.ps1 -Version 1.0.0 -Sign -UiAccess
```

### Option B: PFX Certificate or Hardware Token / EV Cert (Store Thumbprint)

- **Certificate Store / HSM / EV Token**:
  ```powershell
  $env:INBRISK_SIGN_SHA1 = "<CERT_SHA1_THUMBPRINT>"
  .\tools\publish.ps1 -Version 1.0.0 -Sign
  ```
- **PFX File**:
  ```powershell
  $env:INBRISK_SIGN_PFX = "C:\secrets\inbrisk-codesign.pfx"
  $env:INBRISK_SIGN_PFX_PASSWORD = "<PASSWORD>"
  .\tools\publish.ps1 -Version 1.0.0 -Sign
  ```

## 4. Runtime Verification in `Inbrisk.Setup`

`Inbrisk.Setup.AuthenticodeVerifier` uses Win32 `WinVerifyTrust` (`WINTRUST_ACTION_GENERIC_VERIFY_V2`) with `X509Certificate2` signer thumbprint/subject checks. During `SelfInstaller.UpdateAsync`, if `INBRISK_AUTHENTICODE_THUMBPRINT` is set, embedded thumbprints are configured, or the update manifest pins `signerThumbprint`, the extracted `inbrisk.exe` is verified before replacing the installed binary.
