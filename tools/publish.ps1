# publish.ps1 — produce release artifacts:
#   artifacts/release/InbriskSetup.exe   (self-contained installer+CLI, one file)
#   artifacts/release/Inbrisk-x64.zip    (portable)
#   artifacts/release/manifest.json      (release metadata + SHA-256 provenance)
#
# The distributable needs no .NET install — self-contained single-file publish.
# InbriskSetup.exe IS inbrisk.exe: the binary detects its filename and runs the
# install+onboard flow. `/quiet` gives a silent install for AI-driven setup.
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish"
$rel = Join-Path $root "artifacts\release"

Write-Host "Publishing inbrisk $Version ($Rid, self-contained, single-file)..."
dotnet publish (Join-Path $root "src\Inbrisk.Cli\Inbrisk.Cli.csproj") `
    -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -p:InformationalVersion=$Version `
    -o $out | Select-Object -Last 5

$exe = Join-Path $out "inbrisk.exe"
if (!(Test-Path $exe)) { throw "publish produced no inbrisk.exe" }

New-Item -ItemType Directory -Force $rel | Out-Null

# Compile Inno Setup 7 installer if ISCC is installed
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

$issFile = Join-Path $root "installer\inbrisk.iss"
if ($iscc -and (Test-Path $issFile)) {
    Write-Host "Building Inno Setup 7 installer using $iscc..."
    & $iscc "/DMyAppVersion=$Version" "/O$rel" "/FInbriskSetup" $issFile
} else {
    # Fallback to single-file self-contained binary installer
    Copy-Item $exe (Join-Path $rel "InbriskSetup.exe") -Force
}

# Portable zip: just the exe — `inbrisk.exe mcp` works from any absolute path
$zip = Join-Path $rel "Inbrisk-$($Rid.Replace('win-','')).zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $exe -DestinationPath $zip

# --------------------------------------------------------------------------
# F31 opt-in signing hooks — DISABLED by default; no cert/key exists yet.
#
# Authenticode (signtool): set ONE of these before running:
#   $env:INBRISK_SIGN_SHA1 = "<cert thumbprint in the cert store>"
#   $env:INBRISK_SIGN_PFX  = "C:\path\to\codesign.pfx"
#   $env:INBRISK_SIGN_PFX_PASSWORD = "..."   (only with INBRISK_SIGN_PFX)
# Signing happens BEFORE the manifest hashes below so provenance covers the
# shipped bytes.
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty Source -First 1
if (-not $signtool) {
    $signtool = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\signtool.exe"
    ) | ForEach-Object { Resolve-Path $_ -ErrorAction SilentlyContinue } |
        Select-Object -ExpandProperty Path -First 1
}
if (($env:INBRISK_SIGN_SHA1 -or $env:INBRISK_SIGN_PFX) -and $signtool) {
    $signArgs = @("sign", "/fd", "sha256",
        "/tr", "http://timestamp.digicert.com", "/td", "sha256")
    if ($env:INBRISK_SIGN_SHA1) { $signArgs += @("/sha1", $env:INBRISK_SIGN_SHA1) }
    if ($env:INBRISK_SIGN_PFX) {
        $signArgs += @("/f", $env:INBRISK_SIGN_PFX)
        if ($env:INBRISK_SIGN_PFX_PASSWORD) { $signArgs += @("/p", $env:INBRISK_SIGN_PFX_PASSWORD) }
    }
    foreach ($artifact in @($exe, (Join-Path $rel "InbriskSetup.exe"), $zip)) {
        if (Test-Path $artifact) {
            Write-Host "Authenticode signing $artifact"
            & $signtool @signArgs $artifact | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "signtool failed on $artifact" }
        }
    }
} elseif ($env:INBRISK_SIGN_SHA1 -or $env:INBRISK_SIGN_PFX) {
    Write-Warning "signing requested but signtool.exe not found — artifacts stay unsigned"
}

$commit = try { (git -C $root rev-parse HEAD 2>$null).Trim() } catch { $null }
if ([string]::IsNullOrWhiteSpace($commit)) { $commit = "unknown" }

function Sha256($p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }

$manifest = [ordered]@{
    name               = "inbrisk"
    version            = $Version
    buildCommit        = $commit
    buildTimestamp     = [DateTime]::UtcNow.ToString("o")
    architecture       = $Rid
    supportedWindows   = @("Windows 10 1809+", "Windows 11")
    runtime            = "self-contained .NET 8 (no runtime install required)"
    mcpTransport       = "stdio"
    payloadUrl         = Split-Path $zip -Leaf
    payloadSha256      = Sha256 $zip
    files              = [ordered]@{
        "InbriskSetup.exe" = @{ sha256 = Sha256 (Join-Path $rel "InbriskSetup.exe") }
        (Split-Path $zip -Leaf) = @{ sha256 = Sha256 $zip }
        "inbrisk.exe (payload)" = @{ sha256 = Sha256 $exe }
    }
    install            = [ordered]@{
        perUser   = "%LOCALAPPDATA%\Programs\Inbrisk"
        machine   = "C:\Program Files\Inbrisk (inbrisk install --machine, UAC)"
        silent    = "InbriskSetup.exe /quiet"
        uninstall = "Apps & Features, or inbrisk.exe uninstall"
    }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $rel "manifest.json") -Encoding utf8

# F31: detached manifest signature for `inbrisk update` — the updater verifies
# manifest.json against the public key embedded via UpdateSigning.EmbeddedPublicKeyPem.
# Opt-in: set $env:INBRISK_MANIFEST_KEY to an RSA private key (.pem). Requires
# openssl on PATH. Produces manifest.json.sig next to the manifest; ship both.
if ($env:INBRISK_MANIFEST_KEY) {
    $openssl = Get-Command openssl.exe -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source -First 1
    if ($openssl) {
        & $openssl dgst -sha256 -sign $env:INBRISK_MANIFEST_KEY `
            -out (Join-Path $rel "manifest.json.sig") `
            (Join-Path $rel "manifest.json")
        if ($LASTEXITCODE -ne 0) { throw "manifest signing failed" }
        Write-Host "manifest.json.sig written (detached RSA/SHA-256 signature)"
    } else {
        Write-Warning "INBRISK_MANIFEST_KEY set but openssl not found — manifest unsigned"
    }
}
# Key generation for release signing (run once, guard the private key):
#   openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out update-signing-private.pem
#   openssl rsa -in update-signing-private.pem -pubout -out update-signing-public.pem
# then paste update-signing-public.pem into src/Inbrisk.Setup/UpdateSigning.cs
# (EmbeddedPublicKeyPem) and sign manifests with:
#   openssl dgst -sha256 -sign update-signing-private.pem -out manifest.json.sig manifest.json

Write-Host ""
Write-Host "Release artifacts in $rel :"
Get-ChildItem $rel | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
