<#
.SYNOPSIS
    Reusable Authenticode code signing script for Inbrisk binaries and installers.

.DESCRIPTION
    Signs one or more PE files (.exe, .dll, etc.) using SHA-256 and RFC 3161 timestamping
    via Windows SDK signtool.exe, then verifies each signature with `signtool verify /pa /v`.

    Supports three signing modes (in priority order):
      1. Azure Trusted Signing: -AzureMetadataPath / $env:INBRISK_AZURE_SIGNING_METADATA
         (+ Azure.CodeSigning.Dlib.dll via -AzureDlibPath / $env:INBRISK_AZURE_SIGNING_DLIB or auto-discovery)
      2. Certificate store thumbprint: -Thumbprint / $env:INBRISK_SIGN_SHA1
      3. PFX file: -PfxPath / $env:INBRISK_SIGN_PFX (+ -PfxPassword / $env:INBRISK_SIGN_PFX_PASSWORD)

.EXAMPLE
    .\tools\Sign-Artifact.ps1 -Path artifacts\publish\inbrisk.exe -Thumbprint <SHA1>

.EXAMPLE
    $env:INBRISK_SIGN_PFX = "C:\secrets\inbrisk.pfx"
    $env:INBRISK_SIGN_PFX_PASSWORD = "..."
    .\tools\Sign-Artifact.ps1 -Path artifacts\release\InbriskSetup.exe

.EXAMPLE
    $env:INBRISK_AZURE_SIGNING_METADATA = "C:\ci\trusted-signing-metadata.json"
    .\tools\Sign-Artifact.ps1 -Path artifacts\publish\inbrisk.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline = $true)]
    [string[]]$Path,

    [string]$Thumbprint = $env:INBRISK_SIGN_SHA1,
    [string]$PfxPath = $env:INBRISK_SIGN_PFX,
    [string]$PfxPassword = $env:INBRISK_SIGN_PFX_PASSWORD,
    [string]$AzureMetadataPath = $env:INBRISK_AZURE_SIGNING_METADATA,
    [string]$AzureDlibPath = $env:INBRISK_AZURE_SIGNING_DLIB,
    [string]$TimestampUrl = $env:INBRISK_TIMESTAMP_URL,
    [string]$Description = "Inbrisk",
    [string]$DescriptionUrl = "https://github.com/DarkHSoul/inbrisk",
    [switch]$SkipTimestamp,
    [switch]$SkipVerify
)

begin {
    $ErrorActionPreference = "Stop"

    function Find-SignTool {
        $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd -and (Test-Path $cmd.Source)) {
            return $cmd.Source
        }

        $kitRoots = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
            "${env:ProgramFiles}\Windows Kits\10\bin"
        ) | Where-Object { Test-Path $_ }

        foreach ($kitRoot in $kitRoots) {
            $versions = Get-ChildItem -Path $kitRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
                Sort-Object { [version]$_.Name } -Descending
            foreach ($ver in $versions) {
                foreach ($arch in @("x64", "x86", "arm64")) {
                    $candidate = Join-Path $ver.FullName "$arch\signtool.exe"
                    if (Test-Path $candidate) {
                        return $candidate
                    }
                }
            }
        }

        $fallbackCandidates = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\signtool.exe",
            "${env:ProgramFiles(x86)}\Microsoft SDKs\ClickOnce\SignTool\signtool.exe"
        )
        foreach ($candidate in $fallbackCandidates) {
            if (Test-Path $candidate) {
                return $candidate
            }
        }

        throw "signtool.exe not found on PATH or in Windows Kits 10. Install the Windows 10/11 SDK (Signing Tools for Desktop Apps)."
    }

    function Find-AzureSigningDlib {
        param([string]$ExplicitPath)

        if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
            if (!(Test-Path $ExplicitPath)) {
                throw "Azure Trusted Signing dlib not found at '$ExplicitPath'."
            }
            return (Resolve-Path $ExplicitPath).Path
        }

        $searchPatterns = @(
            "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
            "$env:LOCALAPPDATA\Microsoft\TrustedSigningClientTools\Azure.CodeSigning.Dlib.dll",
            "$env:USERPROFILE\.nuget\packages\microsoft.trusted.signing.client\*\bin\x64\Azure.CodeSigning.Dlib.dll",
            "$env:USERPROFILE\.nuget\packages\azure.codesigning.dlib\*\bin\x64\Azure.CodeSigning.Dlib.dll"
        )
        foreach ($pattern in $searchPatterns) {
            $hit = Resolve-Path $pattern -ErrorAction SilentlyContinue |
                Sort-Object Path -Descending |
                Select-Object -First 1
            if ($hit) {
                return $hit.Path
            }
        }

        throw "Azure.CodeSigning.Dlib.dll not found. Install Microsoft.Trusted.Signing.Client or set -AzureDlibPath / `$env:INBRISK_AZURE_SIGNING_DLIB."
    }

    $signTool = Find-SignTool

    $mode = $null
    if (-not [string]::IsNullOrWhiteSpace($AzureMetadataPath)) {
        if (!(Test-Path $AzureMetadataPath)) {
            throw "Azure Trusted Signing metadata file not found: '$AzureMetadataPath'"
        }
        $mode = "AzureTrustedSigning"
        $resolvedAzureDlib = Find-AzureSigningDlib -ExplicitPath $AzureDlibPath
        $resolvedAzureMetadata = (Resolve-Path $AzureMetadataPath).Path
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $TimestampUrl = "http://timestamp.acs.microsoft.com"
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $mode = "CertStore"
        $Thumbprint = ($Thumbprint -replace '\s+', '').ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $TimestampUrl = "http://timestamp.digicert.com"
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        if (!(Test-Path $PfxPath)) {
            throw "PFX file not found: '$PfxPath'"
        }
        $mode = "Pfx"
        $resolvedPfx = (Resolve-Path $PfxPath).Path
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $TimestampUrl = "http://timestamp.digicert.com"
        }
    }
    else {
        throw "No signing credentials provided. Specify -Thumbprint (`$env:INBRISK_SIGN_SHA1), -PfxPath (`$env:INBRISK_SIGN_PFX), or -AzureMetadataPath (`$env:INBRISK_AZURE_SIGNING_METADATA)."
    }

    $baseSignArgs = @("sign", "/v", "/fd", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($Description)) {
        $baseSignArgs += @("/d", $Description)
    }
    if (-not [string]::IsNullOrWhiteSpace($DescriptionUrl)) {
        $baseSignArgs += @("/du", $DescriptionUrl)
    }
    if (-not $SkipTimestamp) {
        $baseSignArgs += @("/tr", $TimestampUrl, "/td", "SHA256")
    }

    switch ($mode) {
        "AzureTrustedSigning" {
            $baseSignArgs += @("/dlib", $resolvedAzureDlib, "/dmdf", $resolvedAzureMetadata)
        }
        "CertStore" {
            $baseSignArgs += @("/sha1", $Thumbprint)
        }
        "Pfx" {
            $baseSignArgs += @("/f", $resolvedPfx)
            if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) {
                $baseSignArgs += @("/p", $PfxPassword)
            }
        }
    }
}

process {
    foreach ($item in $Path) {
        if (!(Test-Path $item)) {
            throw "Artifact to sign not found: '$item'"
        }
        $fullPath = (Resolve-Path $item).Path
        Write-Host "Signing ($mode): $fullPath"
        & $signTool @baseSignArgs $fullPath
        if ($LASTEXITCODE -ne 0) {
            throw "signtool sign failed with exit code $LASTEXITCODE for '$fullPath'"
        }

        if (-not $SkipVerify) {
            Write-Host "Verifying Authenticode signature: $fullPath"
            & $signTool verify /pa /v $fullPath
            if ($LASTEXITCODE -ne 0) {
                throw "signtool verify /pa /v failed with exit code $LASTEXITCODE for '$fullPath'"
            }
        }
    }
}
