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

Write-Host ""
Write-Host "Release artifacts in $rel :"
Get-ChildItem $rel | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
