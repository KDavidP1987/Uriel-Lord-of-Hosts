<#
.SYNOPSIS
    Release-surface sync check for Uriel, Lord of Hosts.

.DESCRIPTION
    Run from the repo root before any chore(release) commit. Verifies:
      1. Uriel.csproj <Version> == thunderstore.toml versionNumber
      2. Root CHANGELOG.md (full/GitHub) has an entry for that version
      3. Package CHANGELOG.md (concise/Thunderstore) has an entry for that version
      4. thunderstore.toml description is within Thunderstore's 250-char limit
      5. Working tree status (informational)

    Exits 1 if any hard check fails, 0 otherwise.

.EXAMPLE
    pwsh tools/preflight.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = @()

$csprojPath        = Join-Path $repoRoot 'Uriel\Uriel\Uriel.csproj'
$tomlPath          = Join-Path $repoRoot 'Uriel\Uriel\thunderstore.toml'
$rootChangelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$pkgChangelogPath  = Join-Path $repoRoot 'Uriel\Uriel\CHANGELOG.md'

# --- 1. Version parity ---
$csprojVersion = ([xml](Get-Content $csprojPath -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tomlRaw = Get-Content $tomlPath -Raw
$tomlVersion = if ($tomlRaw -match 'versionNumber\s*=\s*"([^"]+)"') { $Matches[1] } else { $null }

if (-not $csprojVersion) { $failures += "Could not read <Version> from Uriel.csproj" }
if (-not $tomlVersion)   { $failures += "Could not read versionNumber from thunderstore.toml" }
if ($csprojVersion -and $tomlVersion -and $csprojVersion -ne $tomlVersion) {
    $failures += "VERSION MISMATCH: csproj=$csprojVersion vs thunderstore.toml=$tomlVersion"
}
Write-Host "Version: csproj=$csprojVersion  toml=$tomlVersion"

# --- 2+3. Changelog entries for the current version ---
$version = $csprojVersion
if ($version) {
    $escaped = [regex]::Escape($version)
    if ((Get-Content $rootChangelogPath -Raw) -notmatch "##\s*\[?$escaped\]?") {
        $failures += "Root CHANGELOG.md (full/GitHub) has NO entry for $version"
    } else { Write-Host "Root CHANGELOG.md: entry for $version found" }

    if ((Get-Content $pkgChangelogPath -Raw) -notmatch "##\s*\[?$escaped\]?") {
        $failures += "Package CHANGELOG.md (Thunderstore) has NO entry for $version"
    } else { Write-Host "Package CHANGELOG.md: entry for $version found" }
}

# --- 4. Thunderstore description length (hard limit 250) ---
if ($tomlRaw -match 'description\s*=\s*"([^"]*)"') {
    $len = $Matches[1].Length
    if ($len -gt 250) { $failures += "thunderstore.toml description is $len chars (limit 250)" }
    else { Write-Host "Thunderstore description: $len/250 chars" }
}

# --- 5. Working tree (informational) ---
try {
    $dirty = git -C $repoRoot status --porcelain
    if ($dirty) { Write-Host "NOTE: working tree has uncommitted changes:`n$dirty" }
    else { Write-Host "Working tree clean." }
} catch { Write-Host "NOTE: git status unavailable." }

# --- Result ---
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "PREFLIGHT FAILED:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Reminder: READMEs (root + package) need a manual staleness pass too."
    exit 1
}

Write-Host ""
Write-Host "PREFLIGHT OK. Manual reminder: scan both READMEs (root=GitHub, package=Thunderstore) for staleness before committing." -ForegroundColor Green
exit 0
