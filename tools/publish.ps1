# publish.ps1 — produce release artifacts:
#   artifacts/release/InbriskSetup.exe   (Inno Setup installer or self-contained setup exe)
#   artifacts/release/Inbrisk-x64.zip    (portable)
#   artifacts/release/manifest.json      (release metadata + SHA-256 provenance)
#
# Optional Authenticode signing & uiAccess:
#   -Sign      Sign inbrisk.exe before packaging and sign InbriskSetup.exe / uninstaller.
#              Also auto-enabled when INBRISK_SIGN_SHA1, INBRISK_SIGN_PFX, or
#              INBRISK_AZURE_SIGNING_METADATA is set.
#   -UiAccess  Build with app.uiaccess.manifest (uiAccess="true") for signed
#              Program Files installations.
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$Sign,
    [switch]$UiAccess,
    [string]$Thumbprint = $env:INBRISK_SIGN_SHA1,
    [string]$PfxPath = $env:INBRISK_SIGN_PFX,
    [string]$PfxPassword = $env:INBRISK_SIGN_PFX_PASSWORD,
    [string]$AzureMetadataPath = $env:INBRISK_AZURE_SIGNING_METADATA,
    [string]$AzureDlibPath = $env:INBRISK_AZURE_SIGNING_DLIB,
    [string]$TimestampUrl = $env:INBRISK_TIMESTAMP_URL,
    [switch]$SkipTimestamp
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish"
$rel = Join-Path $root "artifacts\release"
$signScript = Join-Path $PSScriptRoot "Sign-Artifact.ps1"

$hasSigningCreds = -not [string]::IsNullOrWhiteSpace($Thumbprint) -or
    -not [string]::IsNullOrWhiteSpace($PfxPath) -or
    -not [string]::IsNullOrWhiteSpace($AzureMetadataPath)

if ($Sign -and -not $hasSigningCreds) {
    throw "-Sign was requested, but no signing credentials were provided. Set INBRISK_SIGN_SHA1, INBRISK_SIGN_PFX, or INBRISK_AZURE_SIGNING_METADATA (or pass -Thumbprint / -PfxPath / -AzureMetadataPath)."
}

$doSign = $Sign.IsPresent -or $hasSigningCreds

if ($UiAccess -and -not $doSign) {
    Write-Warning "-UiAccess was requested without Authenticode signing. Windows refuses to launch unsigned uiAccess='true' executables."
}

$publishArgs = @(
    "publish", (Join-Path $root "src\Inbrisk.Cli\Inbrisk.Cli.csproj"),
    "-c", $Configuration,
    "-r", $Rid,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-o", $out
)
if ($UiAccess) {
    $publishArgs += "-p:InbriskUiAccess=true"
}

Write-Host "Publishing inbrisk $Version ($Rid, self-contained, single-file, uiAccess=$($UiAccess.IsPresent))..."
& dotnet @publishArgs | Select-Object -Last 5
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $out "inbrisk.exe"
if (!(Test-Path $exe)) { throw "publish produced no inbrisk.exe" }

function Invoke-SignArtifact {
    param([string]$TargetPath)
    $signParams = @{
        Path = $TargetPath
    }
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) { $signParams.Thumbprint = $Thumbprint }
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) { $signParams.PfxPath = $PfxPath }
    if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $signParams.PfxPassword = $PfxPassword }
    if (-not [string]::IsNullOrWhiteSpace($AzureMetadataPath)) { $signParams.AzureMetadataPath = $AzureMetadataPath }
    if (-not [string]::IsNullOrWhiteSpace($AzureDlibPath)) { $signParams.AzureDlibPath = $AzureDlibPath }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $signParams.TimestampUrl = $TimestampUrl }
    if ($SkipTimestamp) { $signParams.SkipTimestamp = $true }
    & $signScript @signParams
}

# Sign payload binary BEFORE packaging into Inno Setup or portable zip
if ($doSign) {
    Invoke-SignArtifact -TargetPath $exe
}

New-Item -ItemType Directory -Force $rel | Out-Null
$setupExe = Join-Path $rel "InbriskSetup.exe"

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
    $isccArgs = @(
        "/DMyAppVersion=$Version",
        "/O$rel",
        "/FInbriskSetup"
    )
    if ($UiAccess) {
        $isccArgs += "/DUiAccess"
    }
    if ($doSign) {
        # Ensure Sign-Artifact.ps1 sees explicit parameter overrides via process env for Inno Setup's SignTool hook
        if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) { $env:INBRISK_SIGN_SHA1 = $Thumbprint }
        if (-not [string]::IsNullOrWhiteSpace($PfxPath)) { $env:INBRISK_SIGN_PFX = $PfxPath }
        if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $env:INBRISK_SIGN_PFX_PASSWORD = $PfxPassword }
        if (-not [string]::IsNullOrWhiteSpace($AzureMetadataPath)) { $env:INBRISK_AZURE_SIGNING_METADATA = $AzureMetadataPath }
        if (-not [string]::IsNullOrWhiteSpace($AzureDlibPath)) { $env:INBRISK_AZURE_SIGNING_DLIB = $AzureDlibPath }
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $env:INBRISK_TIMESTAMP_URL = $TimestampUrl }

        $skipTsArg = if ($SkipTimestamp) { " -SkipTimestamp" } else { "" }
        $isccArgs += "/DSignCert"
        $isccArgs += "/Sinbrisk=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$signScript`"$skipTsArg -Path `$f"
    }
    $isccArgs += $issFile
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
} else {
    # Fallback to single-file self-contained binary installer
    Copy-Item $exe $setupExe -Force
    if ($doSign) {
        Invoke-SignArtifact -TargetPath $setupExe
    }
}

# Portable zip: contains the already-signed (when -Sign) inbrisk.exe
$zip = Join-Path $rel "Inbrisk-$($Rid.Replace('win-','')).zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $exe -DestinationPath $zip

$commit = try { (git -C $root rev-parse HEAD 2>$null).Trim() } catch { $null }
if ([string]::IsNullOrWhiteSpace($commit)) { $commit = "unknown" }

function Sha256($p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }

$manifest = [ordered]@{
    name               = "inbrisk"
    version            = $Version
    buildCommit        = $commit
    buildTimestamp     = [DateTime]::UtcNow.ToString("o")
    architecture       = $Rid
    signed             = $doSign
    uiAccess           = $UiAccess.IsPresent
    supportedWindows   = @("Windows 10 1809+", "Windows 11")
    runtime            = "self-contained .NET 8 (no runtime install required)"
    mcpTransport       = "stdio"
    payloadUrl         = Split-Path $zip -Leaf
    payloadSha256      = Sha256 $zip
    files              = [ordered]@{
        "InbriskSetup.exe" = @{ sha256 = Sha256 $setupExe }
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
# manifest.json against the public key embedded via UpdateSignatureVerifier.
# Opt-in: set $env:INBRISK_MANIFEST_KEY to a private key (.pem). Requires
# openssl on PATH. Produces manifest.json.sig next to the manifest; ship both.
if ($env:INBRISK_MANIFEST_KEY) {
    $openssl = Get-Command openssl.exe -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source -First 1
    if ($openssl) {
        & $openssl dgst -sha256 -sign $env:INBRISK_MANIFEST_KEY `
            -out (Join-Path $rel "manifest.json.sig") `
            (Join-Path $rel "manifest.json")
        if ($LASTEXITCODE -ne 0) { throw "manifest signing failed" }
        Write-Host "manifest.json.sig written (detached SHA-256 signature)"
    } else {
        Write-Warning "INBRISK_MANIFEST_KEY set but openssl not found — manifest unsigned"
    }
}

Write-Host ""
Write-Host "Release artifacts in $rel :"
Get-ChildItem $rel | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
