[CmdletBinding()]
param(
    [Parameter(Position=0)]
    [string]$NewVersion
)

$ErrorActionPreference = "Stop"
$ProjectDir = (Resolve-Path "$PSScriptRoot\..").Path

function Normalize-Version([string]$v) {
    $parts = $v.Trim().Split('.')
    if ($parts.Count -eq 1) { return "$($parts[0]).0.0.0" }
    if ($parts.Count -eq 2) { return "$($parts[0]).$($parts[1]).0.0" }
    if ($parts.Count -eq 3) { return "$($parts[0]).$($parts[1]).$($parts[2]).0" }
    if ($parts.Count -eq 4) { return "$($parts[0]).$($parts[1]).$($parts[2]).$($parts[3])" }
    throw "Invalid version format '$v'. Expected up to 4 dot-separated integers (e.g. 0.8.0.0)."
}

$ManifestFile = Join-Path $ProjectDir "Package.appxmanifest"
$CsprojFile   = Join-Path $ProjectDir "QuickEditor.csproj"
$AppManifest  = Join-Path $ProjectDir "app.manifest"

$CurrentVersion = "Unknown"
if (Test-Path $ManifestFile) {
    $manifestContent = Get-Content $ManifestFile -Raw -Encoding utf8
    if ($manifestContent -match '<Identity\s+[^>]*Version="([0-9\.]+)"') {
        $CurrentVersion = $Matches[1]
    }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "        Quick Editor - Version Management Tool           " -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Current Version: " -NoNewline
Write-Host $CurrentVersion -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

if (-not $NewVersion) {
    $inputVersion = Read-Host "Enter new version (e.g. 0.8.1.0 or 1.0.0.0)"
    if ([string]::IsNullOrWhiteSpace($inputVersion)) {
        Write-Host "No version entered. Aborted." -ForegroundColor Red
        return
    }
    $NewVersion = $inputVersion
}

$Normalized = Normalize-Version $NewVersion

$parts = $Normalized.Split('.')
foreach ($p in $parts) {
    if (-not [int]::TryParse($p, [ref]$null)) {
        Write-Host "Error: '$p' is not a valid integer in version '$Normalized'." -ForegroundColor Red
        return
    }
}

Write-Host "`nUpdating version to: $Normalized..." -ForegroundColor Green

# 1. Update Package.appxmanifest
if (Test-Path $ManifestFile) {
    $content = Get-Content $ManifestFile -Raw -Encoding utf8
    $updated = [regex]::Replace($content, '(<Identity\s+[^>]*Version=")[^"]+(")', "`${1}$Normalized`${2}")
    [IO.File]::WriteAllText($ManifestFile, $updated, [Text.Encoding]::UTF8)
    Write-Host "  [OK] Updated Package.appxmanifest" -ForegroundColor Green
}

# 2. Update QuickEditor.csproj
if (Test-Path $CsprojFile) {
    $content = Get-Content $CsprojFile -Raw -Encoding utf8
    $content = [regex]::Replace($content, '<Version>[^<]+</Version>', "<Version>$Normalized</Version>")
    $content = [regex]::Replace($content, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Normalized</AssemblyVersion>")
    $content = [regex]::Replace($content, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Normalized</FileVersion>")
    [IO.File]::WriteAllText($CsprojFile, $content, [Text.Encoding]::UTF8)
    Write-Host "  [OK] Updated QuickEditor.csproj" -ForegroundColor Green
}

# 3. Update app.manifest
if (Test-Path $AppManifest) {
    $content = Get-Content $AppManifest -Raw -Encoding utf8
    $updated = [regex]::Replace($content, '(<assemblyIdentity\s+[^>]*version=")[^"]+(")', "`${1}$Normalized`${2}")
    [IO.File]::WriteAllText($AppManifest, $updated, [Text.Encoding]::UTF8)
    Write-Host "  [OK] Updated app.manifest" -ForegroundColor Green
}

Write-Host "`nSuccessfully updated all version references to $Normalized!`n" -ForegroundColor Cyan
