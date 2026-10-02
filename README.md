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

You can easily integrate Quick Editor into the Windows 10 & 11 right-click context menu:

- **Option 1 (One-click batch):** Double-click `AddContextMenu.bat` to register or `RemoveContextMenu.bat` to remove.
- **Option 2 (PowerShell):** Run:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\Register-ExplorerMenu.ps1
  ```
  Or to unregister:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\Register-ExplorerMenu.ps1 -Unregister
  ```

This adds **Edit with Quick Editor** (with icon) to audio and video files across Windows 10 and Windows 11 (under the main menu and *Show more options*), and includes Quick Editor in the Windows **Open with** list. For MSIX packaged installations, file associations are automatically registered upon installation.