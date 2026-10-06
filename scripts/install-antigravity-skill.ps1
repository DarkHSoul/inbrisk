$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$SourceSkillDir = Join-Path $RepoRoot "skills\inbrisk"

if (-not (Test-Path $SourceSkillDir)) {
    Write-Error "Source skill directory not found at $SourceSkillDir"
    exit 1
}

$InstalledTargets = @()

# 1. User-global Antigravity skill directory
$GlobalSkillBase = "$env:USERPROFILE\.gemini\antigravity\skills"
if (Test-Path "$env:USERPROFILE\.gemini\antigravity") {
    $GlobalTarget = Join-Path $GlobalSkillBase "inbrisk"
    if (-not (Test-Path $GlobalTarget)) {
        New-Item -ItemType Directory -Path $GlobalTarget -Force | Out-Null
    }
    Copy-Item -Path "$SourceSkillDir\*" -Destination $GlobalTarget -Recurse -Force
    $InstalledTargets += $GlobalTarget
}

# 2. Workspace skill directory
$WorkspaceSkillBase = Join-Path $RepoRoot ".agents\skills"
if (Test-Path $WorkspaceSkillBase) {
    $WorkspaceTarget = Join-Path $WorkspaceSkillBase "inbrisk"
    if (-not (Test-Path $WorkspaceTarget)) {
        New-Item -ItemType Directory -Path $WorkspaceTarget -Force | Out-Null
    }
    Copy-Item -Path "$SourceSkillDir\*" -Destination $WorkspaceTarget -Recurse -Force
    $InstalledTargets += $WorkspaceTarget
}

Write-Host "Inbrisk Antigravity Skill installed successfully to:"
foreach ($target in $InstalledTargets) {
    Write-Host "  - $target"
    if (-not (Test-Path (Join-Path $target "SKILL.md"))) {
        Write-Error "Verification failed: SKILL.md missing in $target"
        exit 1
    }
}

exit 0
