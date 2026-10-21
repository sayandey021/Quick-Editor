# Changelog

## 0.9

- **Studio-style Intermediate Cuts**:
  - Added **"New cut"** (Split at playhead) and **"Delete cut"** controls to the Trim toolbar with Segoe Fluent icons (`\uE8C0` scissors, `\uE74D` trash), allowing users to cut out multiple arbitrary middle/intermediate sections from video and audio files.
  - **Interactive Timeline Cuts**: rendered with high-contrast semi-transparent red overlays, top and bottom red guide borders, draggable red boundary handles, and floating pill badges showing exact cut durations (e.g., `-00:04.2`) with an instant '✕' delete button right on the timeline.
  - **Interactive Cut Manipulation**: drag left and right cut handles to adjust start and end points, drag the cut body to reposition along the timeline, and click any cut to select, inspect, or delete it.
  - **Real-time Live Cut Skipping**: during live playback, the playhead and audio engine automatically and instantaneously jump across cut intervals when reached, providing seamless YouTube Studio-style preview of edited results without waiting for offline rendering.
  - **Synchronized Waveform View**: cut regions are visibly overlaid on both the timeline track and the dedicated full-size audio waveform viewport with cut-dimmed bar coloring.
  - **Multi-Segment FFmpeg Filter Complex Export**: upgraded `MediaExportService` with frame-accurate multi-segment `trim`/`atrim` and `concat` filtergraph pipelines that strip all cut sections and stitch kept segments together seamlessly while applying all video adjustments (crop, color grading, exposure, vibrance, speed) and audio DSP filters (pitch shift, 6-band EQ, reverb, boost, loudness normalization).
  - **Undo/Redo & Reset Integration**: adding, modifying, or removing cuts is fully integrated with `Undo trim & cuts` and `Reset trim & cuts`.
  - **Dynamic Cut Duration Metrics**: duration readout updates dynamically with kept output time, cut count, and total cut duration (e.g. `01:15.2 selected (2 cuts: -00:14.8) / 02:00.0`).
- **Fixed Playhead Hit-Testing Over Trim and Cut Handles**:
  - Prioritized the red playhead knob and top ruler area during pointer clicks and drags on `TimelineCanvas`. Clicking the red circular knob or clicking anywhere in the top time ruler now always scrubs the playhead rather than incorrectly capturing trim start/end or cut boundary handles when overlapping (such as at `00:00.0`).
  - Constrained trim and cut handle hitboxes strictly to the waveform track area (`Y >= trackTop - 5` to `trackTop + trackHeight + 6`), allowing users to grab the trim handle in the track body while grabbing the playhead knob at the top edge.
- **Video Text & Image Overlays**:
  - **Overlays Tab & Layer Manager**: added a third tab to the adjustments panel with live count pill badge, featuring "+ Add text" and "+ Add image" buttons, an interactive layer list showing layer type, preview summary, selection state, and quick delete buttons.
  - **Interactive Direct Drag, Viewport Resize & Alignment Presets**: click and drag text or image overlays directly across the video preview surface in real-time. Image overlays feature 4 interactive corner resize handles directly in the viewport for intuitive click-and-drag scaling while preserving aspect ratio and frame boundaries, automatically synchronizing with the inspector size slider. Includes a 3x3 alignment preset grid (`TL`, `TC`, `TR`, `CL`, `Center`, `CR`, `BL`, `BC`, `BR`) for instantaneous snapping to corners, edges, or center watermark positions.
  - **Zero-Flicker In-Place Rendering & Bitmap Caching**: eliminated all visual flickering when moving, resizing, or changing sides by introducing in-memory `BitmapImage` caching and persistent XAML visual tree element reuse, updating layout transforms and bounds in-place without rebuilding or re-decoding elements.
  - **Rich Text Customization**: customizable text content, font size slider (12-120px), font color picker with curated palette swatches (White, Yellow, Red, Green, Cyan, Blue, Magenta, Black), optional background banner box with customizable box opacity, and overall layer opacity slider.
  - **Image Watermarks & Logos**: supports PNG, JPG, JPEG, WEBP, and BMP images with automatic dimension probing, customizable relative size slider (5%-80% frame width), change image button, and alpha opacity blending.
  - **Selective Display Timing**: choose between displaying the overlay throughout the entire video or restricting it to custom start and end timestamps (with handy "At playhead" convenience buttons for setting start or end to current playback position). Overlays dynamically appear and disappear during live video playback and timeline scrubbing according to their active time ranges.
  - **FFmpeg High-Performance Render Export**: integrated into both single-segment and multi-segment concat export pipelines using hardware/software accelerated `drawtext` with Windows fontfile resolution, special character escaping, proportional text scaling, and alpha-blended `overlay` filter graphs for crisp exported output.
  - **Refined Tab Title Layout & Checkbox Alignment**: widened sidebar layout column and tightened button padding so the "Overlays" tab title and count pill badge never clip or truncate, added contrasting frosted pill background to the count badge when the tab is selected, and geometrically centered checkbox label baselines with the checkbox glyph squares (`VerticalContentAlignment="Center"`, `MinHeight="0"`, `Padding="8,0,0,0"`).
  - **Intelligent Magnetic Snapping & Visual Guidelines**: moving or resizing text and image overlays automatically snaps to frame center axes (Center X, Center Y) and frame outer boundaries (Left, Right, Top, Bottom) within a 9px threshold. Dynamically renders high-contrast amber dashed guidelines and floating indicator badges ("Center X", "Center Y", "Left", "Right", "Top", "Bottom") during active drag operations for precision alignment.
  - **Undo/Redo Support**: overlay modifications are fully tracked in the unified undo stack.
- **Enhanced Video Crop & Aspect Ratio Switching**:
  - **Horizontal Scrolling Ratio Cards with Aspect Diagrams**: replaced the tall vertical dropdown menu with a sleek, side-by-side horizontal scrolling card selector (`CropRatiosScrollViewer`). Each card features a miniature proportional aspect ratio diagram box above its label (e.g. 16:9 wide rectangle, 9:16 vertical phone box, 1:1 square, 21:9 ultrawide bar, 4:5 portrait, and dashed freeform box).
  - **Smooth Mouse Wheel & Touch Scrolling**: added horizontal mouse wheel navigation (`CropRatiosScrollViewer_PointerWheelChanged`) allowing effortless scrolling through ratio cards with the mouse wheel or touch gestures without cluttering the UI.
  - **One-Click Instant Viewport Switching**: clicking any ratio card automatically activates crop mode if not already active and immediately recalculates, reshapes, and centers the crop box in the viewport with active accent border and glow styling.
  - **Instant Interactive Crop Points & Handles**: entering Crop Mode ("Crop video") immediately displays all 8 interactive resize handles (4 corners + 4 edge midpoints) along with the active crop outline, enabling immediate adjustment, moving, or resizing without needing to drag to select first.
  - **12 Comprehensive Aspect Ratio Presets**:
    - `Free`: Unconstrained freeform crop (dashed icon)
    - `16:9`: Landscape / YouTube / TV (`1.7777778`)
    - `9:16`: Vertical / Shorts / Reels / TikTok (`0.5625`)
    - `1:1`: Square / Instagram Post (`1.0`)
    - `4:5`: Portrait / Instagram Feed (`0.8`)
    - `5:4`: Large Format (`1.25`)
    - `4:3`: Standard TV / Tablet (`1.3333333`)
    - `3:4`: Portrait SD (`0.75`)
    - `21:9`: Ultrawide / CinemaScope (`2.3333333`)
    - `2:1`: Univisium / Modern Streaming (`2.0`)
    - `3:2`: Classic 35mm Photography (`1.5`)
    - `2:3`: Portrait 35mm Photography (`0.6666667`)
  - **Hardened Bounds Math & Crash Prevention**: fixed `ArgumentException` crash caused by floating-point precision overshoots where `min > max` in `Math.Clamp` during handle resizing and full-edge ratio clamping. Introduced `SafeClamp` and robust `try/catch` guards across all pointer event handlers and Win2D rendering callbacks to guarantee 100% crash resilience during active crop editing.
- **Interactive Timeline Zoom & Horizontal Panning**:
  - **Trim Toolbar Zoom Controls**: added dedicated zoom controls in the Trim toolbar with Segoe Fluent icons: Zoom Out (`\uE71F`), continuous Zoom Slider (`1.0x` to `10.0x` in 0.25x increments), Zoom In (`\uE71E`), and a 1-click Reset/Fit button (`\uE895`) displaying a live magnification percentage readout (`100%` - `1000%`).
  - **Smooth Horizontal Canvas Scrolling**: wrapped `TimelineCanvas` in a smooth horizontal `ScrollViewer` (`TimelineScrollViewer`), scaling track layout width up to 10x while preserving exact frame and millisecond precision for trims and cuts.
  - **Mouse Wheel & Trackpad Gestures**: zoom in/out anywhere over the timeline canvas using `Ctrl + MouseWheel` centered around the mouse cursor or playhead, with standard mouse wheel and trackpad horizontal scrolling enabled when zoomed in.
  - **Dynamic Ruler Ticks & Timestamps**: dynamically scales ruler tick density and major timestamp labels (`00:00.0`) based on zoom level to ensure ruler markings remain sharp, legible, and uncluttered from high-level overviews down to fine sub-second edits.
  - **Live Playhead Auto-Following**: during video and audio playback, the timeline viewport automatically scrolls to keep the red playhead centered and visible when zoomed in.
  - **Performance-Optimized Waveform Rendering**: capped real-time audio waveform bar generation to 1200 adaptive bars, ensuring fluid 60 FPS timeline scrubbing and zooming without UI freezes or memory overhead.
  - **Interactive Horizontal Scrollbar When Zoomed**: enabled fluent horizontal scrollbar visibility (`HorizontalScrollBarVisibility="Auto"`) on `TimelineScrollViewer` whenever the timeline is magnified beyond 100%. The scrollbar thumb can be directly dragged with the mouse or clicked along the track for quick scrubbing across long zoomed files, seamlessly hides when fit to 100% or reset, and has dedicated bottom clearance (`Height="76"`, `CornerRadius="8"`) to avoid overlapping trim handles or playback markers.
  - **Refined Toolbar Sliders Layout & Responsive Fit**: redesigned both the Volume and Timeline Zoom slider pills in the trim toolbar to ensure they never clip or get cut off. Switched both pill columns to `Width="Auto"` with an elastic spacer before duration metrics, eliminated oversized slider track padding (`Padding="0"` with clean `Height="24"` and `MinHeight="0"`), and added direct mouse wheel scroll support to both pills (`VolumeControl_PointerWheelChanged` and `TimelineZoomControl_PointerWheelChanged`) for instant volume and zoom adjustments on hover.
  - **Sub-Pixel Trim & Cut Precision**: all trim start/end brackets, intermediate cut intervals, draggable cut handles, floating duration pill badges, and delete buttons scale with sub-pixel accuracy at all zoom levels.

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
