# Changelog

## 0.8

- Fixed window movement and dragging: replaced manual `PointerPressed` message dispatch with native `WM_NCHITTEST` subclassing and `AppWindow.TitleBar.SetDragRectangles`, restoring seamless single-motion title bar dragging without requiring a prior click to activate or focus the window, while keeping all interactive title bar controls fully responsive.
- Cleaned preview status bar text: removed audio output device diagnostic label (`Wave Mapper (System Default)`), displaying a clean and uncluttered `"Preview ready"` message.
- Added vertical and horizontal axis snapping for video crop: moving, resizing, and drawing crop boxes now automatically snaps to the frame's vertical and horizontal center axes as well as outer boundaries, rendering high-contrast amber alignment guidelines and pill badges ("Center X", "Center Y", "Left", "Right", "Top", "Bottom") during active drag operations for precision centering and alignment.
- Integrated custom Fluent title bar and matching window caption buttons: replaced harsh native Windows system buttons with custom styled Minimize, Maximize/Restore, and Close buttons (`CornerRadius="6"`, 32x32) that perfectly match the app's aesthetic and vertical alignment, featuring Segoe Fluent icons (`\uE921`, `\uE922` / `\uE923`, `\uE8BB`), dynamic theme ink colors, square with rounded corners hover highlights, and smooth red close hover effects while preserving native window dragging and double-click to maximize.
- Improved theme toggle with borderless design, fluid animation, and Windows default theme compliance: removed the square rectangular button border in favor of a sleek circular icon button with subtle hover highlights, implemented a 360-degree rotation and spring-bounce scale morph animation when switching between Moon and Sun modes, and configured the application to automatically detect and obey the Windows system default theme on startup with live dynamic synchronization.
- Updated playhead to vibrant red across all themes: styled both the timeline scrubber (stem and circular head) and the audio waveform viewport playhead in persistent high-visibility red (`#FF3B30`), ensuring instant contrast against light and dark media backgrounds alike.
- Fixed volume slider knob overlapping and clipping: added internal horizontal padding (`Padding="10,0,10,0"`) and adjusted slider layout dimensions on `VolumeSlider` so the thumb knob and circular hover halo at 100% volume have full breathing room and render as a complete circle without being clipped flat by the control boundaries or colliding with the percentage text.
- Enlarged Taskbar and Start Menu icon: cropped excess outer transparent padding and scaled the icon full-bleed edge-to-edge across all ICO resolutions (including high-DPI steps 16, 20, 24, 30, 32, 36, 40, 48, 64, 72, 96, 128, 256), matching standard Windows app icon dimensions on the taskbar and start menu.
- Styled timeline with rounded corners: wrapped `TimelineCanvas` in a `Border` with `CornerRadius="8"` and rendered all timeline tracks, unselected trims, and selection highlights using smooth `CornerRadius` segments (`AddTrackSegment`) with rounded line caps on guide lines, eliminating sharp rectangular corners across the trim interface.
- Set default app icon across the entire application: generated multi-size Windows icon (`Assets/AppIcon.ico`) with full resolution hierarchy (16x16 through 256x256), embedded as PE application icon in `QuickEditor.csproj`, assigned to `AppWindow.SetIcon` for title bar and taskbar, added in-app header branding image, and linked to File Explorer context menu.

## 0.7

- Fixed audio playback failure (`E_NOINTERFACE` / `InvalidCastException` on `IMFSourceReader`): eliminated the probe read during `AudioPitchEngine.Load` that contaminated Media Foundation COM apartment state across background playback threads, upgraded fallback decode to native `WaveFileReader`, and added automatic `MediaPlayer` fallback recovery on audio driver/device playback stop.
- Fixed audio playback routing: configured NAudio `WaveOutEvent` as primary output targeting the Windows system default device (`WAVE_MAPPER`), dynamically outputting to headphones, monitors, Bluetooth, or speakers without format mismatches or WASAPI device-binding lockouts.
- Added `ThreadSafeSampleProvider` in `AudioPitchEngine` to prevent concurrent access between real-time audio threads and seek operations on `MediaFoundationReader`.
- Fixed speed change clock tracking in `AudioPitchEngine.SetPitchAndSpeed`, maintaining seamless playback position without artificial drift triggers.
- Added automatic fallback to `MediaPlayer` audio output if physical output devices fail to initialize or play, ensuring media playback is never completely silent.
- Displayed active audio output device in the preview status bar for immediate visual confirmation.
- Fixed live audio preview routing: made `AudioPitchEngine` the exclusive audio pipeline during playback, preventing Windows Media Player from bypassing real-time EQ, pitch shifting, reverb, and volume boost.
- Fixed `MediaPlayerElement` auto-play and dual-player collision: removed duplicate `Preview.Source` assignments and disabled AutoPlay on `Preview`.
- Fixed FFmpeg standard error pipe buffer deadlock in `AudioPitchEngine.ExtractAudioToWav` by consuming stderr asynchronously.
- Improved audio format detection and fallback in `AudioPitchEngine.Load`, ensuring zero-sample decoding attempts automatically fallback to FFmpeg extraction.
- Fixed export audio track detection in `GetAdjustments`, ensuring audio filters (pitch, EQ, reverb, volume boost, normalize) are always applied to exported media.
- Fixed live audio preview when changing EQ, Pitch, Volume Boost, Normalization, and Reverb: eliminated the 40Hz seek loop caused by pre-buffered `MediaFoundationReader` read position.
- Implemented high-precision Stopwatch-based playback clock tracking in `AudioPitchEngine` for synchronized real-time audio playback without dropouts or stutter.
- Upgraded `BypassablePitchShifterSampleProvider` to independent dual-channel (stereo) phase vocoder processing using `NAudio.Dsp.SmbPitchShifter`, preventing cross-channel phase distortion.
- Fixed preview edit lockout: adjusting any audio or video slider or scrubbing the timeline now automatically exits the static pre-rendered preview and returns to live real-time editing.
- Added automatic FFmpeg fallback audio decode in `AudioPitchEngine.Load` for 100% format support, including OGG, Opus, and unsupported containers.
- Fixed export audio adjustments in `MediaExportService`: corrected input trim duration (`-t durationSeconds`), placed loudness normalization before volume boost, and added universal `atempo` support.
- Fixed native access violation crash (0xC0000005) caused by unsafe Media Foundation resampler buffer repositioning during seeks.

## 0.6

- Added dedicated **Video** and **Audio** adjustment tabs in the sidebar for instant one-click switching, preventing audio controls from being hidden below the video sliders.
- Added bit-perfect zero-latency bypass for unshifted audio (`PitchFactor = 1.0`), ensuring pristine audio quality when adjusting EQ, Reverb, or Volume Boost.
- Added `volatile` memory synchronization across all real-time audio thread filters (EQ, Reverb, Volume Boost, Limiter) for instantaneous live response.
- Fixed playback seek thrashing by relaxing drift sync tolerance to 0.85s, eliminating audio dropouts and stutter during live playback.
- Added visual **Audio Active Badge** indicator and **Live DSP Active** status display in the UI.
- Added real-time audio pitch shifting during playback with instant response when moving the pitch slider (-12 to +12 semitones).
- Integrated NAudio SMB pitch-shifting DSP pipeline with low-latency FFT buffers for artifact-free audio shifting.
- Implemented combined pitch and varispeed playback rate scaling (0.5x to 2.0x) with live pitch compensation.
- Added 6-band parametric peaking equalizer (80 Hz, 240 Hz, 750 Hz, 2.2 kHz, 6 kHz, 12 kHz) with real-time biquad filtering and presets (Flat, Bass Boost, Vocal Enhance, Treble Boost, Loudness, Acoustic).
- Added studio algorithmic reverberation with real-time controls for Reverb Mix and Room Size.
- Added Volume Boost (up to 300% / +10 dB) backed by an analog-modeled soft-knee peak limiter to eliminate harsh digital clipping.
- Added audio normalization supporting live peak scaling, "Auto-calibrate peak" analysis, and EBU R128 standard loudness normalization on export.
- Upgraded audio output to WASAPI Shared Mode targeting the default audio endpoint (USB, Bluetooth, internal speakers) with automatic format negotiation.
- Added high-quality `MediaFoundationResampler` (quality 60) with automatic buffer repositioning on timeline seek.
- Added automatic lip-sync drift correction between video decoding and the audio pitch engine during active playback.
- Updated `RunQuickEditor.bat` to eliminate file-locking errors and ensure source code changes are always incrementally compiled before launching.

## 0.5

- Replaced Win2D contrast and exposure effects with a custom calibrated `ColorMatrixEffect` to eliminate color dulling and dark block artifacts.
- Implemented Lightroom-grade midpoint contrast math preserving true blacks and highlight roll-off.
- Enhanced Temperature and Tint color balance controls alongside Hue, Saturation, Brightness, and Contrast.
- Improved video crop workflow: exiting crop mode with "Done" now automatically zooms into and frames only the cropped region within the viewport.
- Optimized Win2D frame server rendering for smooth, real-time preview of color adjustments and cropped framing.

## 0.4

- Improved crop mode so the crop overlay remains active and usable throughout the drag interaction.
- Fixed crop rectangle handling to avoid empty or invalid selections during resize and move operations.
- Added clearer crop overlay behavior for drawing, moving, and resizing selections directly on the video viewport.
- Kept crop presets and freeform crop options working with a more stable selection state.
- Polished the editor viewport layout and scrollbar behavior to keep the crop area and controls usable without overlap.
- Continued refinements to the light theme and editor styling for better consistency in the preview surface.

## 0.3

- Replaced separate trim sliders with a single timeline featuring a time ruler, range handles, and a movable playhead.
- Added FFmpeg-generated waveforms to the trim timeline and the main preview viewport for audio files.
- Removed the player transport overlay; playback and volume controls now live in the trim toolbar.
- Added coordinated Light and Dark themes with Windows 11-inspired blue, white, and gray colors.
- Refined timeline and toolbar sizing, waveform visibility, and empty-preview styling.
- Fixed WinUI startup issues with slider initialization and control resources.


## 0.2

- Added video crop, brightness, and saturation adjustments.
- Added audio pitch and playback-speed adjustments.
- Applied the same edits to rendered previews and exported copies.
- Added trim undo/reset and reset controls for video and audio adjustments.
- Added inline playback volume, percentage display, and one-click mute/unmute with remembered volume.
- Added container-aware FFmpeg encoding for supported audio and video formats.

## 0.1

- Created the C# WinUI 3 desktop app and its media preview window.
- Added audio/video file opening, drag-and-drop, and direct file-argument launch.
- Added basic trim, range preview, and FFmpeg export to a new copy with progress and cancellation.
- Added optional per-user File Explorer registration for **Edit with Quick Editor**.
- Added `RunQuickEditor.bat` for quick launch, with optional media-file argument support.
