[CmdletBinding()]
param(
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$ProjectDir = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $OutputDir) {
    $OutputDir = Join-Path $ProjectDir "bin\ShellExtension"
}

$candidates = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
)

$vcvarsPath = $null
foreach ($cand in $candidates) {
    if (Test-Path $cand) {
        $vcvarsPath = $cand
        break
    }
}

if (-not $vcvarsPath) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -find "VC\Auxiliary\Build\vcvars64.bat"
        if ($found) {
            foreach ($f in $found) {
                if (Test-Path $f) { $vcvarsPath = $f; break }
            }
        }
    }
}

if (-not $vcvarsPath) {
    throw "vcvars64.bat not found in candidate paths or vswhere."
}
Write-Host "Using vcvars64: $vcvarsPath" -ForegroundColor DarkGray

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$resolvedOutputDir = (Resolve-Path $OutputDir).Path
$srcFile = Join-Path $ProjectDir "src\ShellExtension\QuickEditorShell.cpp"
$defFile = Join-Path $ProjectDir "src\ShellExtension\QuickEditorShell.def"
$outDll = Join-Path $resolvedOutputDir "QuickEditorShell.dll"

$cmd = @"
call "$vcvarsPath" >nul 2>&1
cl.exe /O2 /W3 /D_USRDLL /D_WINDLL /EHsc /MD /std:c++17 "$srcFile" /link /DLL /DEF:"$defFile" /OUT:"$outDll" shlwapi.lib shell32.lib ole32.lib user32.lib /SUBSYSTEM:WINDOWS /NOLOGO
"@

$tempBat = Join-Path $env:TEMP "build_shell_ext_$([Guid]::NewGuid().ToString('N')).bat"
Set-Content -Path $tempBat -Value $cmd -Encoding Ascii

try {
    Write-Host "Compiling QuickEditorShell.dll (x64)..." -ForegroundColor Cyan
    & cmd.exe /c $tempBat
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE"
    }

    if (Test-Path $outDll) {
        $size = (Get-Item $outDll).Length / 1KB
        Write-Host ("Successfully built: {0} ({1:N1} KB)" -f $outDll, $size) -ForegroundColor Green
    } else {
        throw "Output DLL not found: $outDll"
    }
}
finally {
    Remove-Item -Path $tempBat -Force -ErrorAction SilentlyContinue
    # Clean up intermediate obj/exp/lib in working directory
    Remove-Item "QuickEditorShell.obj", "QuickEditorShell.exp", "QuickEditorShell.lib" -Force -ErrorAction SilentlyContinue
}
