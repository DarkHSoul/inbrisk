$ErrorActionPreference = "Stop"

$InstalledCli = "$env:LOCALAPPDATA\Inbrisk\bin\inbrisk-cli.exe"
$DevCli = Join-Path $PSScriptRoot "..\..\..\target\release\inbrisk-cli.exe"

$CliPath = $null
if (Test-Path $InstalledCli) {
    $CliPath = $InstalledCli
} elseif (Test-Path $DevCli) {
    $CliPath = (Resolve-Path $DevCli).Path
} else {
    $Command = Get-Command "inbrisk-cli.exe" -ErrorAction SilentlyContinue
    if ($Command) {
        $CliPath = $Command.Source
    }
}

if (-not $CliPath) {
    Write-Error "inbrisk-cli.exe not found at installed location ($InstalledCli) or dev location ($DevCli)."
    exit 1
}

Write-Host "Using CLI binary: $CliPath"
& $CliPath fast-path verify --json
exit $LASTEXITCODE
