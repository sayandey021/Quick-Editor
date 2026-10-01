# Quick Editor

A lightweight WinUI 3 desktop editor for opening, previewing, and exporting one audio or video file. It supports trim, video crop and color adjustment, and audio pitch and speed changes. Export always writes to a separate destination; the source file is never used as the output.

## Requirements

- Windows 10 version 1809 or later
- .NET 8 SDK
- FFmpeg available as `ffmpeg.exe` on `PATH`, with the `libx264` encoder and `rubberband` audio filter enabled

## Build and run

```powershell
dotnet build .\QuickEditor.csproj -p:Platform=x64
dotnet run --project .\QuickEditor.csproj -p:Platform=x64
```

You can also double-click `RunQuickEditor.bat`, or pass it a media file path to open that file directly.

Open a media file with **Open file** or drop it onto the preview. Use the trim sliders and the media-specific controls to make edits, then choose **Preview edits** to render and play the selected range with those settings applied. Choose **Export copy** to save the same edits in a new file. Processing runs asynchronously, reports progress, and can be canceled. A temporary file is finalized at the selected destination only after FFmpeg succeeds.

The Windows media player handles audio/video playback. FFmpeg performs trim, crop, color, pitch, and speed processing so the edited preview matches the export. NAudio is referenced for future audio-specific preview and waveform work.

## File Explorer menu

The Explorer action is an optional per-user registration. After building, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Register-ExplorerMenu.ps1 -AppPath .\bin\x64\Debug\net8.0-windows10.0.19041.0\QuickEditor.exe
```

This adds **Edit with Quick Editor** to audio and video context menus for the current Windows user. The app reads the selected file from its command-line arguments. Remove the menu with:

```powershell
powershell -ExecutionPolicy Bypass -File .\Register-ExplorerMenu.ps1 -Unregister
```