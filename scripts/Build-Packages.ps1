[CmdletBinding()]
param(
    [Parameter(Position=0)]
    [ValidateSet("MSIX", "EXE", "All", "msix", "exe", "all")]
    [string]$Target
)

$ErrorActionPreference = "Stop"
$ProjectDir = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $ProjectDir

# 1. Kill any running QuickEditor instances
Get-Process -Name "QuickEditor" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Get current version from Package.appxmanifest
$ManifestFile = Join-Path $ProjectDir "Package.appxmanifest"
$Version = "0.8.0.0"
if (Test-Path $ManifestFile) {
    $content = Get-Content $ManifestFile -Raw -Encoding utf8
    if ($content -match '<Identity\s+[^>]*Version="([0-9\.]+)"') {
        $Version = $Matches[1]
    }
}

# 3. Interactive prompt if no target specified
if (-not $Target) {
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "        Quick Editor - Build & Packaging Tool             " -ForegroundColor White
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host " Application: Quick Editor" -ForegroundColor Gray
    Write-Host " Version:     $Version" -ForegroundColor Yellow
    Write-Host " Package ID:  Saayan.QuickEditor (Publisher: SaayanSoft)" -ForegroundColor Gray
    Write-Host " Target Arch: x64 (Self-Contained .NET 8 & Windows App SDK)" -ForegroundColor Gray
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host " Select build target:" -ForegroundColor White
    Write-Host "   [1] Standalone Portable EXE (Folder + ZIP archive)"
    Write-Host "   [2] MSIX Package & Store Upload (.msix + .msixupload)"
    Write-Host "   [3] Build BOTH (MSIX + Standalone EXE)"
    Write-Host "   [4] Cancel"
    Write-Host "==========================================================" -ForegroundColor Cyan
    $choice = Read-Host " Enter choice [1-4] (default: 3)"
    if ([string]::IsNullOrWhiteSpace($choice)) { $choice = "3" }

    switch ($choice) {
        "1" { $Target = "EXE" }
        "2" { $Target = "MSIX" }
        "3" { $Target = "All" }
        default {
            Write-Host "Build cancelled." -ForegroundColor Yellow
            return
        }
    }
}

$Target = $Target.ToUpperInvariant()

# Function to locate MSBuild
function Get-MSBuildPath {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"
        if ($found -and (Test-Path $found[0])) {
            return $found[0]
        }
    }

    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

# ----------------- BUILD SHELL EXTENSION DLL -----------------
Write-Host "`n>>> Building Windows 11 Shell Extension DLL (QuickEditorShell.dll)..." -ForegroundColor Cyan
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\Build-ShellExtension.ps1"
} catch {
    Write-Host "Warning: Shell extension build failed: $_" -ForegroundColor Yellow
}

# ----------------- BUILD STANDALONE EXE -----------------
if ($Target -eq "EXE" -or $Target -eq "ALL") {
    Write-Host "`n>>> [1/2] Building Standalone Portable EXE..." -ForegroundColor Cyan
    $ExeOutputDir = Join-Path $ProjectDir "bin\publish\QuickEditor_${Version}_x64_Portable"
    $ZipOutput = Join-Path $ProjectDir "bin\publish\QuickEditor_${Version}_x64_Portable.zip"

    if (Test-Path $ExeOutputDir) {
        Remove-Item -Recurse -Force $ExeOutputDir -ErrorAction SilentlyContinue
    }
    if (Test-Path $ZipOutput) {
        Remove-Item -Force $ZipOutput -ErrorAction SilentlyContinue
    }

    $publishArgs = @(
        "publish",
        "QuickEditor.csproj",
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained",
        "-p:WindowsPackageType=None",
        "-p:Platform=x64",
        "-o", $ExeOutputDir,
        "--nologo"
    )

    Write-Host "Running: dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet $publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "EXE build failed with exit code $LASTEXITCODE." -ForegroundColor Red
        if ($Target -ne "ALL") { return }
    } else {
        # Copy shell extension DLL and helpers to portable directory
        $shellDll = Join-Path $ProjectDir "bin\ShellExtension\QuickEditorShell.dll"
        if (Test-Path $shellDll) {
            Copy-Item -Path $shellDll -Destination $ExeOutputDir -Force
        }
        Copy-Item -Path (Join-Path $ProjectDir "AddContextMenu.bat") -Destination $ExeOutputDir -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $ProjectDir "RemoveContextMenu.bat") -Destination $ExeOutputDir -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $ProjectDir "Register-ExplorerMenu.ps1") -Destination $ExeOutputDir -Force -ErrorAction SilentlyContinue

        Write-Host "Creating ZIP archive: $ZipOutput..." -ForegroundColor Gray
        Compress-Archive -Path "$ExeOutputDir\*" -DestinationPath $ZipOutput -Force

        $zipSize = (Get-Item $ZipOutput).Length / 1MB
        Write-Host "  [OK] Portable EXE Folder: $ExeOutputDir" -ForegroundColor Green
        Write-Host ("  [OK] Portable ZIP:        $ZipOutput ({0:N1} MB)" -f $zipSize) -ForegroundColor Green
    }
}

# ----------------- BUILD MSIX & STORE PACKAGE -----------------
if ($Target -eq "MSIX" -or $Target -eq "ALL") {
    Write-Host "`n>>> [2/2] Building MSIX Package & Store Upload Container..." -ForegroundColor Cyan

    $msbuild = Get-MSBuildPath
    if (-not $msbuild) {
        Write-Host "Error: MSBuild.exe was not found. Please install Visual Studio with .NET desktop / WinUI workload." -ForegroundColor Red
        return
    }

    Write-Host "Using MSBuild: $msbuild" -ForegroundColor DarkGray

    $msbuildArgs = @(
        "QuickEditor.csproj",
        "/restore",
        "/p:Configuration=Release",
        "/p:Platform=x64",
        "/p:WindowsPackageType=MSIX",
        "/p:SelfContained=true",
        "/p:GenerateAppxPackageOnBuild=true",
        "/p:AppxPackageSigningEnabled=false",
        "/p:UapAppxPackageBuildMode=StoreUpload",
        "/p:AppxSymbolPackageEnabled=false",
        "/m",
        "/nologo"
    )

    Write-Host "Running MSBuild for MSIX..." -ForegroundColor DarkGray
    & $msbuild $msbuildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "MSIX build failed with exit code $LASTEXITCODE." -ForegroundColor Red
        return
    }

    $appPackagesDir = Join-Path $ProjectDir "AppPackages"
    $msixUpload = Join-Path $appPackagesDir "QuickEditor_${Version}_x64.msixupload"
    $msixTest = Join-Path $appPackagesDir "QuickEditor_${Version}_x64_Test\QuickEditor_${Version}_x64.msix"

    Write-Host "`n==========================================================" -ForegroundColor Cyan
    Write-Host "                MSIX BUILD SUCCESSFUL!                    " -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan

    if (Test-Path $msixUpload) {
        $uploadSize = (Get-Item $msixUpload).Length / 1MB
        Write-Host ("  [STORE UPLOAD]:  $msixUpload ({0:N1} MB)" -f $uploadSize) -ForegroundColor Yellow
        Write-Host "                   -> Upload this .msixupload directly to Partner Center" -ForegroundColor Gray
    }
    if (Test-Path $msixTest) {
        $testSize = (Get-Item $msixTest).Length / 1MB
        Write-Host ("  [SIDELOAD MSIX]: $msixTest ({0:N1} MB)" -f $testSize) -ForegroundColor Green
        Write-Host "                   -> Sideload/test package with Install.ps1" -ForegroundColor Gray
    }
    Write-Host "==========================================================`n" -ForegroundColor Cyan
}

Write-Host "Build completed successfully!" -ForegroundColor Green
