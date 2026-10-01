param(
    [string]$AppPath,
    [switch]$Unregister
)

$categories = @("audio", "video")

if (-not $Unregister) {
    if ([string]::IsNullOrWhiteSpace($AppPath)) {
        throw "Pass the path to QuickEditor.exe with -AppPath."
    }

    $resolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
    if (-not (Test-Path -LiteralPath $resolvedAppPath -PathType Leaf)) {
        throw "The application executable was not found: $resolvedAppPath"
    }
}

foreach ($category in $categories) {
    $verbPath = "HKCU:\Software\Classes\SystemFileAssociations\$category\shell\QuickEditor"

    if ($Unregister) {
        Remove-Item -LiteralPath $verbPath -Recurse -Force -ErrorAction SilentlyContinue
        continue
    }

    New-Item -Path $verbPath -Force | Out-Null
    Set-Item -LiteralPath $verbPath -Value "Edit with Quick Editor"
    New-ItemProperty -LiteralPath $verbPath -Name "Icon" -Value $resolvedAppPath -PropertyType String -Force | Out-Null

    $commandPath = Join-Path $verbPath "command"
    New-Item -Path $commandPath -Force | Out-Null
    Set-Item -LiteralPath $commandPath -Value ('"{0}" "%1"' -f $resolvedAppPath)
}

if ($Unregister) {
    Write-Host "Quick Editor was removed from the current user's audio and video context menus."
} else {
    Write-Host "Edit with Quick Editor is available for audio and video files for the current user."
}