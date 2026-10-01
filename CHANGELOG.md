# Changelog

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
