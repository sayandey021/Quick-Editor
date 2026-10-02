param(
    [string]$AppPath,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

$supportedExtensions = @(
    ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v",
    ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".opus"
)
$categories = @("audio", "video")

# Unregister handler
if ($Unregister) {
    Write-Host "Removing Quick Editor from Explorer context menus..." -ForegroundColor Cyan

    foreach ($category in $categories) {
        $verbPath = "HKCU:\Software\Classes\SystemFileAssociations\$category\shell\QuickEditor"
        Remove-Item -LiteralPath $verbPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($ext in $supportedExtensions) {
        $extVerbPath = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\QuickEditor"
        Remove-Item -LiteralPath $extVerbPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    $appRegPath = "HKCU:\Software\Classes\Applications\QuickEditor.exe"
    Remove-Item -LiteralPath $appRegPath -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "Quick Editor was successfully removed from context menus." -ForegroundColor Green
    return
}

# Auto-detect AppPath if not provided
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $searchPaths = @(
        (Join-Path $scriptDir "QuickEditor.exe"),
        (Join-Path $scriptDir "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\QuickEditor.exe"),
        (Join-Path $scriptDir "bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\QuickEditor.exe"),
        (Join-Path $scriptDir "bin\x64\Debug\net8.0-windows10.0.19041.0\QuickEditor.exe")
    )

    $publishExes = Get-ChildItem -Path (Join-Path $scriptDir "bin\publish") -Filter "QuickEditor.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
    if ($publishExes) {
        $searchPaths += $publishExes
    }

    foreach ($candidate in $searchPaths) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $AppPath = $candidate
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($AppPath)) {
        throw "Could not automatically locate QuickEditor.exe. Please build the project or specify -AppPath 'path\to\QuickEditor.exe'."
    }
}

$resolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
if (-not (Test-Path -LiteralPath $resolvedAppPath -PathType Leaf)) {
    throw "The application executable was not found: $resolvedAppPath"
}

Write-Host "Registering Quick Editor context menu for: $resolvedAppPath" -ForegroundColor Cyan

# Resolve Icon
$iconPath = $resolvedAppPath
$candidateIco = Join-Path (Split-Path $resolvedAppPath) "Assets\AppIcon.ico"
$fallbackIco = Join-Path $scriptDir "Assets\AppIcon.ico"
if (Test-Path -LiteralPath $candidateIco) {
    $iconPath = $candidateIco
} elseif (Test-Path -LiteralPath $fallbackIco) {
    $iconPath = $fallbackIco
}

# 1. Register under SystemFileAssociations categories (audio, video)
foreach ($category in $categories) {
    $verbPath = "HKCU:\Software\Classes\SystemFileAssociations\$category\shell\QuickEditor"
    New-Item -Path $verbPath -Force | Out-Null
    Set-Item -LiteralPath $verbPath -Value "Edit with Quick Editor"
    New-ItemProperty -LiteralPath $verbPath -Name "Icon" -Value $iconPath -PropertyType String -Force | Out-Null

    $commandPath = Join-Path $verbPath "command"
    New-Item -Path $commandPath -Force | Out-Null
    Set-Item -LiteralPath $commandPath -Value ('"{0}" "%1"' -f $resolvedAppPath)
}

# 2. Register under individual file extensions
foreach ($ext in $supportedExtensions) {
    $extVerbPath = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\QuickEditor"
    New-Item -Path $extVerbPath -Force | Out-Null
    Set-Item -LiteralPath $extVerbPath -Value "Edit with Quick Editor"
    New-ItemProperty -LiteralPath $extVerbPath -Name "Icon" -Value $iconPath -PropertyType String -Force | Out-Null

    $commandPath = Join-Path $extVerbPath "command"
    New-Item -Path $commandPath -Force | Out-Null
    Set-Item -LiteralPath $commandPath -Value ('"{0}" "%1"' -f $resolvedAppPath)
}

# 3. Register under Applications for Windows 'Open with' menu
$appReg = "HKCU:\Software\Classes\Applications\QuickEditor.exe"
New-Item -Path $appReg -Force | Out-Null
Set-Item -LiteralPath $appReg -Value "Quick Editor"
New-ItemProperty -LiteralPath $appReg -Name "FriendlyAppName" -Value "Quick Editor" -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $appReg -Name "Icon" -Value $iconPath -PropertyType String -Force | Out-Null

$appCommand = Join-Path $appReg "shell\open\command"
New-Item -Path $appCommand -Force | Out-Null
Set-Item -LiteralPath $appCommand -Value ('"{0}" "%1"' -f $resolvedAppPath)

$suppTypes = Join-Path $appReg "SupportedTypes"
New-Item -Path $suppTypes -Force | Out-Null
foreach ($ext in $supportedExtensions) {
    New-ItemProperty -LiteralPath $suppTypes -Name $ext -Value "" -PropertyType String -Force | Out-Null
}

Write-Host "Success! 'Edit with Quick Editor' is now added to the right-click menu for all audio and video files (Windows 10 & Windows 11)." -ForegroundColor Green