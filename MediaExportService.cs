using System.Diagnostics;
using System.Globalization;

namespace QuickEditor;

public sealed record CropRegion(double X, double Y, double Width, double Height);

public enum OverlayType
{
    Text,
    Image
}

public sealed class MediaOverlay
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public OverlayType Type { get; set; } = OverlayType.Text;
    public bool IsVisible { get; set; } = true;

    // Text properties
    public string Text { get; set; } = "Sample Text";
    public double FontSize { get; set; } = 32;
    public string FontColorHex { get; set; } = "#FFFFFF";
    public bool HasBackground { get; set; } = false;
    public string BackgroundColorHex { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.6;

    // Image properties
    public string? ImagePath { get; set; }
    public double ImageWidthPercent { get; set; } = 25;

    // Position & opacity (0.0 to 1.0)
    public double NormalizedX { get; set; } = 0.5;
    public double NormalizedY { get; set; } = 0.5;
    public double Opacity { get; set; } = 1.0;

    // Timing
    public bool ShowEntireVideo { get; set; } = true;
    public double StartSeconds { get; set; } = 0.0;
    public double EndSeconds { get; set; } = 5.0;

    public MediaOverlay Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = Type,
        IsVisible = IsVisible,
        Text = Text,
        FontSize = FontSize,
        FontColorHex = FontColorHex,
        HasBackground = HasBackground,
        BackgroundColorHex = BackgroundColorHex,
        BackgroundOpacity = BackgroundOpacity,
        ImagePath = ImagePath,
        ImageWidthPercent = ImageWidthPercent,
        NormalizedX = NormalizedX,
        NormalizedY = NormalizedY,
        Opacity = Opacity,
        ShowEntireVideo = ShowEntireVideo,
        StartSeconds = StartSeconds,
        EndSeconds = EndSeconds
    };
}

public sealed record MediaAdjustments(
    CropRegion Crop,
    double Temperature,
    double Tint,
    double Brightness,
    double Contrast,
    double Highlights,
    double Shadows,
    double Vibrance,
    double Saturation,
    double Hue,
    double PitchSemitones,
    double PlaybackSpeed,
    double VolumeBoost = 1.0,
    bool Normalize = false,
    double[]? EqBands = null,
    double ReverbMix = 0.0,
    double ReverbRoomSize = 0.5,
    bool HasAudioTrack = true,
    IReadOnlyList<MediaOverlay>? Overlays = null,
    int VideoNaturalWidth = 1920,
    int VideoNaturalHeight = 1080);

public sealed class MediaExportService
{
    public Task ExportTrimAsync(
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        bool isVideo,
        MediaAdjustments adjustments,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
        => ExportTrimAsync(inputPath, outputPath, [(startSeconds, startSeconds + durationSeconds)], isVideo, adjustments, progress, cancellationToken);

    public async Task ExportTrimAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<(double Start, double End)> keptSegments,
        bool isVideo,
        MediaAdjustments adjustments,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The source file could not be found.", inputPath);
        }

        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a new file name so the original stays untouched.");
        }

        var totalDuration = keptSegments.Sum(s => Math.Max(0, s.End - s.Start));
        if (totalDuration <= 0)
        {
            throw new InvalidOperationException("Choose a trim range longer than zero seconds.");
        }

        var ffmpeg = FindExecutable("ffmpeg.exe");
        if (ffmpeg is null)
        {
            throw new InvalidOperationException("FFmpeg was not found. Install FFmpeg and add ffmpeg.exe to your PATH, then restart Quick Editor.");
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var extension = Path.GetExtension(outputPath);
        var playbackSpeed = Math.Clamp(adjustments.PlaybackSpeed, 0.5, 2.0);
        var outputDuration = totalDuration / playbackSpeed;
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileNameWithoutExtension(outputPath)}-{Guid.NewGuid():N}.tmp{extension}");
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-y");
        info.ArgumentList.Add("-nostats");
        info.ArgumentList.Add("-progress");
        info.ArgumentList.Add("pipe:1");

        var activeOverlays = adjustments.Overlays?
            .Where(o => o.IsVisible && (o.Type == OverlayType.Text ? !string.IsNullOrWhiteSpace(o.Text) : (!string.IsNullOrWhiteSpace(o.ImagePath) && File.Exists(o.ImagePath))))
            .ToList() ?? [];
        var imageOverlays = activeOverlays.Where(o => o.Type == OverlayType.Image && File.Exists(o.ImagePath)).ToList();

        if (keptSegments.Count <= 1)
        {
            var single = keptSegments.Count == 1 ? keptSegments[0] : (Start: 0.0, End: totalDuration);
            var startSeconds = single.Start;
            var durationSeconds = single.End - single.Start;

            info.ArgumentList.Add("-ss");
            info.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(inputPath);
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add(durationSeconds.ToString("0.###", CultureInfo.InvariantCulture));

            foreach (var img in imageOverlays)
            {
                info.ArgumentList.Add("-i");
                info.ArgumentList.Add(img.ImagePath!);
            }

            if (isVideo && imageOverlays.Count > 0)
            {
                AddSingleSegmentOverlayComplexFilter(info, adjustments, playbackSpeed, activeOverlays, imageOverlays);
            }
            else
            {
                if (isVideo)
                {
                    info.ArgumentList.Add("-map");
                    info.ArgumentList.Add("0:v:0?");
                    if (adjustments.HasAudioTrack)
                    {
                        info.ArgumentList.Add("-map");
                        info.ArgumentList.Add("0:a:0?");
                    }
                }
                else
                {
                    info.ArgumentList.Add("-map");
                    info.ArgumentList.Add("0:a:0");
                }

                AddFilterArguments(info, adjustments, playbackSpeed, isVideo);
            }
        }
        else
        {
            // Multiple kept segments (intermediate cuts removed via filter_complex concat)
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(inputPath);
            foreach (var img in imageOverlays)
            {
                info.ArgumentList.Add("-i");
                info.ArgumentList.Add(img.ImagePath!);
            }
            AddComplexConcatFilterArguments(info, adjustments, keptSegments, playbackSpeed, isVideo, activeOverlays, imageOverlays);
        }

        AddEncodingArguments(info, extension, isVideo);
        info.ArgumentList.Add(temporaryPath);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("FFmpeg could not be started.");
            }

            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan("out_time_ms=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsed))
                {
                    progress?.Report(Math.Clamp(elapsed / (outputDuration * 1_000_000d) * 100, 0, 99));
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFmpeg couldn't export this file." : error.Trim());
            }

            File.Move(temporaryPath, outputPath, true);
            progress?.Report(100);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AddSingleSegmentOverlayComplexFilter(
        ProcessStartInfo info,
        MediaAdjustments adjustments,
        double playbackSpeed,
        List<MediaOverlay> activeOverlays,
        List<MediaOverlay> imageOverlays)
    {
        var filterParts = new List<string>();
        var baseVideoFilters = BuildBaseVideoFilters(adjustments, playbackSpeed);
        var curStream = "[0:v]";
        if (baseVideoFilters.Count > 0)
        {
            filterParts.Add($"[0:v]{string.Join(',', baseVideoFilters)}[vbase]");
            curStream = "[vbase]";
        }

        for (var idx = 0; idx < activeOverlays.Count; idx++)
        {
            var ovl = activeOverlays[idx];
            var nextStream = $"[vovl_{idx}]";
            if (ovl.Type == OverlayType.Text)
            {
                var textFilter = BuildDrawTextFilter(ovl, adjustments.VideoNaturalHeight);
                filterParts.Add($"{curStream}{textFilter}{nextStream}");
                curStream = nextStream;
            }
            else if (ovl.Type == OverlayType.Image && !string.IsNullOrWhiteSpace(ovl.ImagePath) && File.Exists(ovl.ImagePath))
            {
                var imgInputIndex = 1 + imageOverlays.IndexOf(ovl);
                var targetWidth = Math.Max(16, (int)Math.Round(adjustments.VideoNaturalWidth * (ovl.ImageWidthPercent / 100.0) / 2.0) * 2);
                var opacity = Math.Clamp(ovl.Opacity, 0.05, 1.0).ToString("0.##", CultureInfo.InvariantCulture);
                var normX = Math.Clamp(ovl.NormalizedX, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
                var normY = Math.Clamp(ovl.NormalizedY, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);

                filterParts.Add($"[{imgInputIndex}:v]scale={targetWidth}:-1,format=rgba,colorchannelmixer=aa={opacity}[ovl_scaled_{idx}]");
                var enableClause = ovl.ShowEntireVideo
                    ? string.Empty
                    : $":enable='between(t,{ovl.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)},{ovl.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)})'";
                filterParts.Add($"{curStream}[ovl_scaled_{idx}]overlay=x=(W-w)*{normX}:y=(H-h)*{normY}{enableClause}{nextStream}");
                curStream = nextStream;
            }
        }

        string? audioMap = null;
        if (adjustments.HasAudioTrack)
        {
            var audioFilters = BuildAudioFilters(adjustments, playbackSpeed);
            if (audioFilters.Count > 0)
            {
                filterParts.Add($"[0:a]{string.Join(',', audioFilters)}[aout]");
                audioMap = "[aout]";
            }
            else
            {
                audioMap = "0:a:0?";
            }
        }

        info.ArgumentList.Add("-filter_complex");
        info.ArgumentList.Add(string.Join(';', filterParts));
        info.ArgumentList.Add("-map");
        info.ArgumentList.Add(curStream);
        if (audioMap is not null)
        {
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add(audioMap);
        }
    }

    private static void AddComplexConcatFilterArguments(
        ProcessStartInfo info,
        MediaAdjustments adjustments,
        IReadOnlyList<(double Start, double End)> keptSegments,
        double playbackSpeed,
        bool isVideo,
        List<MediaOverlay> activeOverlays,
        List<MediaOverlay> imageOverlays)
    {
        var filterParts = new List<string>();
        var count = keptSegments.Count;

        if (isVideo)
        {
            for (var i = 0; i < count; i++)
            {
                var seg = keptSegments[i];
                var sStart = seg.Start.ToString("0.###", CultureInfo.InvariantCulture);
                var sEnd = seg.End.ToString("0.###", CultureInfo.InvariantCulture);
                filterParts.Add($"[0:v]trim=start={sStart}:end={sEnd},setpts=PTS-STARTPTS[v{i}]");
                if (adjustments.HasAudioTrack)
                {
                    filterParts.Add($"[0:a]atrim=start={sStart}:end={sEnd},asetpts=PTS-STARTPTS[a{i}]");
                }
            }

            if (adjustments.HasAudioTrack)
            {
                var concatInputs = string.Concat(Enumerable.Range(0, count).Select(i => $"[v{i}][a{i}]"));
                filterParts.Add($"{concatInputs}concat=n={count}:v=1:a=1[vcat][acat]");
            }
            else
            {
                var concatInputs = string.Concat(Enumerable.Range(0, count).Select(i => $"[v{i}]"));
                filterParts.Add($"{concatInputs}concat=n={count}:v=1:a=0[vcat]");
            }

            var baseVideoFilters = BuildBaseVideoFilters(adjustments, playbackSpeed);
            var videoMap = "[vcat]";
            if (baseVideoFilters.Count > 0)
            {
                filterParts.Add($"[vcat]{string.Join(',', baseVideoFilters)}[vadj]");
                videoMap = "[vadj]";
            }

            if (activeOverlays.Count > 0)
            {
                var curStream = videoMap;
                for (var idx = 0; idx < activeOverlays.Count; idx++)
                {
                    var ovl = activeOverlays[idx];
                    var nextStream = $"[vovl_{idx}]";
                    if (ovl.Type == OverlayType.Text)
                    {
                        var textFilter = BuildDrawTextFilter(ovl, adjustments.VideoNaturalHeight);
                        filterParts.Add($"{curStream}{textFilter}{nextStream}");
                        curStream = nextStream;
                    }
                    else if (ovl.Type == OverlayType.Image && !string.IsNullOrWhiteSpace(ovl.ImagePath) && File.Exists(ovl.ImagePath))
                    {
                        var imgInputIndex = 1 + imageOverlays.IndexOf(ovl);
                        var targetWidth = Math.Max(16, (int)Math.Round(adjustments.VideoNaturalWidth * (ovl.ImageWidthPercent / 100.0) / 2.0) * 2);
                        var opacity = Math.Clamp(ovl.Opacity, 0.05, 1.0).ToString("0.##", CultureInfo.InvariantCulture);
                        var normX = Math.Clamp(ovl.NormalizedX, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
                        var normY = Math.Clamp(ovl.NormalizedY, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);

                        filterParts.Add($"[{imgInputIndex}:v]scale={targetWidth}:-1,format=rgba,colorchannelmixer=aa={opacity}[ovl_scaled_{idx}]");
                        var enableClause = ovl.ShowEntireVideo
                            ? string.Empty
                            : $":enable='between(t,{ovl.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)},{ovl.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)})'";
                        filterParts.Add($"{curStream}[ovl_scaled_{idx}]overlay=x=(W-w)*{normX}:y=(H-h)*{normY}{enableClause}{nextStream}");
                        curStream = nextStream;
                    }
                }
                videoMap = curStream;
            }

            string? audioMap = null;
            if (adjustments.HasAudioTrack)
            {
                var audioFilters = BuildAudioFilters(adjustments, playbackSpeed);
                audioMap = "[acat]";
                if (audioFilters.Count > 0)
                {
                    filterParts.Add($"[acat]{string.Join(',', audioFilters)}[aout]");
                    audioMap = "[aout]";
                }
            }

            info.ArgumentList.Add("-filter_complex");
            info.ArgumentList.Add(string.Join(';', filterParts));
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add(videoMap);
            if (audioMap is not null)
            {
                info.ArgumentList.Add("-map");
                info.ArgumentList.Add(audioMap);
            }
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var seg = keptSegments[i];
                var sStart = seg.Start.ToString("0.###", CultureInfo.InvariantCulture);
                var sEnd = seg.End.ToString("0.###", CultureInfo.InvariantCulture);
                filterParts.Add($"[0:a]atrim=start={sStart}:end={sEnd},asetpts=PTS-STARTPTS[a{i}]");
            }

            var concatInputs = string.Concat(Enumerable.Range(0, count).Select(i => $"[a{i}]"));
            filterParts.Add($"{concatInputs}concat=n={count}:v=0:a=1[acat]");

            var audioFilters = BuildAudioFilters(adjustments, playbackSpeed);
            var audioMap = "[acat]";
            if (audioFilters.Count > 0)
            {
                filterParts.Add($"[acat]{string.Join(',', audioFilters)}[aout]");
                audioMap = "[aout]";
            }

            info.ArgumentList.Add("-filter_complex");
            info.ArgumentList.Add(string.Join(';', filterParts));
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add(audioMap);
        }
    }

    private static void AddFilterArguments(ProcessStartInfo info, MediaAdjustments adjustments, double playbackSpeed, bool isVideo)
    {
        if (isVideo)
        {
            var videoFilters = BuildVideoFilters(adjustments, playbackSpeed);
            if (videoFilters.Count > 0)
            {
                info.ArgumentList.Add("-vf");
                info.ArgumentList.Add(string.Join(',', videoFilters));
            }
        }

        if (adjustments.HasAudioTrack)
        {
            var audioFilters = BuildAudioFilters(adjustments, playbackSpeed);
            if (audioFilters.Count > 0)
            {
                info.ArgumentList.Add("-af");
                info.ArgumentList.Add(string.Join(',', audioFilters));
            }
        }
    }

    private static string EscapeDrawText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\'': sb.Append(@"\'"); break;
                case ':': sb.Append(@"\:"); break;
                case ';': sb.Append(@"\;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string BuildDrawTextFilter(MediaOverlay overlay, int videoHeight)
    {
        var normX = Math.Clamp(overlay.NormalizedX, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
        var normY = Math.Clamp(overlay.NormalizedY, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
        var escapedText = EscapeDrawText(overlay.Text);
        var fontPath = File.Exists(@"C:\Windows\Fonts\segoeui.ttf")
            ? @"C\:/Windows/Fonts/segoeui.ttf"
            : @"C\:/Windows/Fonts/arial.ttf";

        var colorHex = overlay.FontColorHex.TrimStart('#');
        var opacity = Math.Clamp(overlay.Opacity, 0.05, 1.0).ToString("0.##", CultureInfo.InvariantCulture);
        var fontColor = $"0x{colorHex}@{opacity}";

        var relSize = Math.Clamp(overlay.FontSize / 720.0, 0.015, 0.35).ToString("0.####", CultureInfo.InvariantCulture);
        var filter = $"drawtext=text='{escapedText}':fontfile='{fontPath}':fontsize=trunc(h*{relSize}):fontcolor={fontColor}:x=(w-text_w)*{normX}:y=(h-text_h)*{normY}";

        if (overlay.HasBackground)
        {
            var bgHex = overlay.BackgroundColorHex.TrimStart('#');
            var bgAlpha = Math.Clamp(overlay.BackgroundOpacity, 0.05, 1.0).ToString("0.##", CultureInfo.InvariantCulture);
            filter += $":box=1:boxcolor=0x{bgHex}@{bgAlpha}:boxborderw=8";
        }

        if (!overlay.ShowEntireVideo)
        {
            var s = Math.Max(0, overlay.StartSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            var e = Math.Max(overlay.StartSeconds + 0.1, overlay.EndSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            filter += $":enable='between(t,{s},{e})'";
        }

        return filter;
    }

    private static List<string> BuildVideoFilters(MediaAdjustments adjustments, double playbackSpeed)
    {
        var videoFilters = BuildBaseVideoFilters(adjustments, playbackSpeed);
        if (adjustments.Overlays is not null)
        {
            var textOverlays = adjustments.Overlays.Where(o => o.IsVisible && o.Type == OverlayType.Text && !string.IsNullOrWhiteSpace(o.Text));
            foreach (var to in textOverlays)
            {
                videoFilters.Add(BuildDrawTextFilter(to, adjustments.VideoNaturalHeight));
            }
        }
        return videoFilters;
    }

    private static List<string> BuildBaseVideoFilters(MediaAdjustments adjustments, double playbackSpeed)
    {
        var videoFilters = new List<string>();
        var crop = adjustments.Crop;
        var cropX = Math.Clamp(crop.X, 0, 0.999);
        var cropY = Math.Clamp(crop.Y, 0, 0.999);
        var cropWidth = Math.Clamp(crop.Width, 0.001, 1 - cropX);
        var cropHeight = Math.Clamp(crop.Height, 0.001, 1 - cropY);
        if (cropX > 0.001 || cropY > 0.001 || cropWidth < 0.999 || cropHeight < 0.999)
        {
            var x = cropX.ToString("0.######", CultureInfo.InvariantCulture);
            var y = cropY.ToString("0.######", CultureInfo.InvariantCulture);
            var width = cropWidth.ToString("0.######", CultureInfo.InvariantCulture);
            var height = cropHeight.ToString("0.######", CultureInfo.InvariantCulture);
            videoFilters.Add($"crop=trunc(iw*{width}/2)*2:trunc(ih*{height}/2)*2:trunc(iw*{x}/2)*2:trunc(ih*{y}/2)*2");
        }

        // 1. White Balance: Temperature & Tint (Lightroom grade)
        var temp = Math.Clamp(adjustments.Temperature, -100, 100);
        if (Math.Abs(temp) > 0.5)
        {
            var kelvin = temp > 0
                ? Math.Round(6500.0 - (temp / 100.0) * 3300.0)
                : Math.Round(6500.0 + (Math.Abs(temp) / 100.0) * 6500.0);
            videoFilters.Add($"colortemperature=temperature={kelvin.ToString("0", CultureInfo.InvariantCulture)}:pl=0.5");
        }

        var tint = Math.Clamp(adjustments.Tint, -100, 100);
        if (Math.Abs(tint) > 0.5)
        {
            var factor = tint / 100.0;
            var rm = (factor * 0.15).ToString("0.###", CultureInfo.InvariantCulture);
            var gm = (-factor * 0.25).ToString("0.###", CultureInfo.InvariantCulture);
            var bm = (factor * 0.12).ToString("0.###", CultureInfo.InvariantCulture);
            videoFilters.Add($"colorbalance=rm={rm}:gm={gm}:bm={bm}:pl=1");
        }

        // 2. Exposure, Contrast & Saturation (Lightroom tone curve)
        var brightness = Math.Clamp(adjustments.Brightness, -0.5, 0.5);
        var saturation = Math.Clamp(adjustments.Saturation, 0, 2);
        var contrast = Math.Clamp(adjustments.Contrast, -1, 1);
        var eqContrast = Math.Clamp(1.0 + contrast * 0.55, 0.20, 2.20);
        if (Math.Abs(brightness) > 0.001 || Math.Abs(contrast) > 0.01 || Math.Abs(saturation - 1) > 0.001)
        {
            videoFilters.Add($"eq=brightness={brightness.ToString("0.###", CultureInfo.InvariantCulture)}:contrast={eqContrast.ToString("0.###", CultureInfo.InvariantCulture)}:saturation={saturation.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        // 3. Highlights & Shadows (Lightroom S-Curve with Contrast coupling)
        var shadows = Math.Clamp(adjustments.Shadows, -1, 1);
        var highlights = Math.Clamp(adjustments.Highlights, -1, 1);
        var sCurveShadowDelta = (shadows * 0.12) - (contrast * 0.08);
        var sCurveHighlightDelta = (highlights * 0.12) + (contrast * 0.08);
        if (Math.Abs(sCurveShadowDelta) > 0.005 || Math.Abs(sCurveHighlightDelta) > 0.005)
        {
            var sPointY = Math.Clamp(0.25 + sCurveShadowDelta, 0.05, 0.45);
            var hPointY = Math.Clamp(0.75 + sCurveHighlightDelta, 0.55, 0.95);
            videoFilters.Add($"curves=all='0/0 0.25/{sPointY.ToString("0.###", CultureInfo.InvariantCulture)} 0.5/0.5 0.75/{hPointY.ToString("0.###", CultureInfo.InvariantCulture)} 1/1'");
        }

        // 4. Vibrance
        var vibrance = Math.Clamp(adjustments.Vibrance, -1, 1);
        if (Math.Abs(vibrance) > 0.01)
        {
            videoFilters.Add($"vibrance=intensity={vibrance.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        // 5. Hue
        var hue = Math.Clamp(adjustments.Hue, -180, 180);
        if (Math.Abs(hue) > 0.1)
        {
            videoFilters.Add($"hue=h={hue.ToString("0.#", CultureInfo.InvariantCulture)}");
        }

        if (Math.Abs(playbackSpeed - 1) > 0.001)
        {
            videoFilters.Add($"setpts=PTS/{playbackSpeed.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return videoFilters;
    }

    private static List<string> BuildAudioFilters(MediaAdjustments adjustments, double playbackSpeed)
    {
        var audioFilters = new List<string>();
        if (!adjustments.HasAudioTrack)
        {
            return audioFilters;
        }

        var pitch = Math.Pow(2, adjustments.PitchSemitones / 12);
        if (Math.Abs(playbackSpeed - 1) > 0.001 || Math.Abs(pitch - 1) > 0.001)
        {
            if (Math.Abs(pitch - 1) <= 0.001)
            {
                audioFilters.Add($"atempo={playbackSpeed.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            else
            {
                audioFilters.Add(
                    $"rubberband=tempo={playbackSpeed.ToString("0.###", CultureInfo.InvariantCulture)}:pitch={pitch.ToString("0.#####", CultureInfo.InvariantCulture)}");
            }
        }

        // 6-Band Equalizer (80, 240, 750, 2200, 6000, 12000 Hz)
        if (adjustments.EqBands is not null && adjustments.EqBands.Length >= 6)
        {
            double[] centerFreqs = [80, 240, 750, 2200, 6000, 12000];
            for (int i = 0; i < 6 && i < adjustments.EqBands.Length; i++)
            {
                var g = adjustments.EqBands[i];
                if (Math.Abs(g) > 0.1)
                {
                    audioFilters.Add($"equalizer=f={centerFreqs[i].ToString(CultureInfo.InvariantCulture)}:width_type=q:w=1.0:g={g.ToString("0.#", CultureInfo.InvariantCulture)}");
                }
            }
        }

        // Reverb multi-tap reflections
        if (adjustments.ReverbMix > 0.01)
        {
            var mix = Math.Clamp(adjustments.ReverbMix, 0.01, 1.0);
            var room = Math.Clamp(adjustments.ReverbRoomSize, 0.1, 1.0);
            var inGain = (1.0 - mix * 0.25).ToString("0.##", CultureInfo.InvariantCulture);
            var outGain = (0.75 + mix * 0.35).ToString("0.##", CultureInfo.InvariantCulture);
            var d1 = Math.Max(10, (int)Math.Round(25 * room));
            var d2 = Math.Max(20, (int)Math.Round(50 * room));
            var d3 = Math.Max(30, (int)Math.Round(75 * room));
            var d4 = Math.Max(40, (int)Math.Round(110 * room));
            var dec1 = Math.Clamp(0.55 * mix, 0.05, 0.9).ToString("0.##", CultureInfo.InvariantCulture);
            var dec2 = Math.Clamp(0.40 * mix, 0.05, 0.85).ToString("0.##", CultureInfo.InvariantCulture);
            var dec3 = Math.Clamp(0.28 * mix, 0.05, 0.8).ToString("0.##", CultureInfo.InvariantCulture);
            var dec4 = Math.Clamp(0.18 * mix, 0.05, 0.75).ToString("0.##", CultureInfo.InvariantCulture);
            audioFilters.Add($"aecho={inGain}:{outGain}:{d1}|{d2}|{d3}|{d4}:{dec1}|{dec2}|{dec3}|{dec4}");
        }

        // Normalize first to calibrated reference level
        if (adjustments.Normalize)
        {
            audioFilters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        // Volume Boost applied with soft limiter
        if (adjustments.VolumeBoost > 1.01)
        {
            var boost = Math.Clamp(adjustments.VolumeBoost, 1.0, 3.0);
            audioFilters.Add($"volume={boost.ToString("0.##", CultureInfo.InvariantCulture)}");
            audioFilters.Add("alimiter=limit=0.98");
        }

        return audioFilters;
    }

    private static void AddEncodingArguments(ProcessStartInfo info, string extension, bool isVideo)
    {
        if (isVideo)
        {
            info.ArgumentList.Add("-c:v");
            var videoCodec = extension.ToLowerInvariant() switch
            {
                ".avi" => "mpeg4",
                ".wmv" => "wmv2",
                ".webm" => "libvpx-vp9",
                _ => "libx264"
            };
            info.ArgumentList.Add(videoCodec);
            if (videoCodec == "libx264")
            {
                info.ArgumentList.Add("-preset");
                info.ArgumentList.Add("ultrafast");
                info.ArgumentList.Add("-crf");
                info.ArgumentList.Add("20");
            }
            else if (videoCodec == "libvpx-vp9")
            {
                info.ArgumentList.Add("-deadline");
                info.ArgumentList.Add("realtime");
                info.ArgumentList.Add("-cpu-used");
                info.ArgumentList.Add("8");
            }

            info.ArgumentList.Add("-c:a");
            info.ArgumentList.Add(extension.ToLowerInvariant() switch
            {
                ".avi" => "libmp3lame",
                ".wmv" => "wmav2",
                ".webm" => "libopus",
                _ => "aac"
            });
            if (videoCodec == "libx264" || videoCodec == "libvpx-vp9")
            {
                info.ArgumentList.Add("-b:a");
                info.ArgumentList.Add("192k");
            }

            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add("-movflags");
                info.ArgumentList.Add("+faststart");
            }

            return;
        }

        info.ArgumentList.Add("-vn");
        var codec = extension.ToLowerInvariant() switch
        {
            ".mp3" => "libmp3lame",
            ".wav" => "pcm_s16le",
            ".flac" => "flac",
            ".ogg" => "libvorbis",
            ".wma" => "wmav2",
            ".opus" => "libopus",
            _ => "aac"
        };
        info.ArgumentList.Add("-c:a");
        info.ArgumentList.Add(codec);
        if (codec == "libmp3lame")
        {
            info.ArgumentList.Add("-q:a");
            info.ArgumentList.Add("2");
        }
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), name))
            .FirstOrDefault(File.Exists);
    }
}