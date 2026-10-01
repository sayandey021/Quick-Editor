using System.Diagnostics;
using System.Globalization;

namespace QuickEditor;

public sealed record CropRegion(double X, double Y, double Width, double Height);

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
    double ReverbRoomSize = 0.5);

public sealed class MediaExportService
{
    public async Task ExportTrimAsync(
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
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

        if (durationSeconds <= 0)
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
        var outputDuration = durationSeconds / playbackSpeed;
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
        info.ArgumentList.Add("-ss");
        info.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(inputPath);
        info.ArgumentList.Add("-t");
        info.ArgumentList.Add(outputDuration.ToString("0.###", CultureInfo.InvariantCulture));
        if (isVideo)
        {
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add("0:v:0?");
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add("0:a:0?");
        }
        else
        {
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add("0:a:0");
        }

        AddFilterArguments(info, adjustments, playbackSpeed, isVideo);
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

    private static void AddFilterArguments(ProcessStartInfo info, MediaAdjustments adjustments, double playbackSpeed, bool isVideo)
    {
        if (isVideo)
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

            if (videoFilters.Count > 0)
            {
                info.ArgumentList.Add("-vf");
                info.ArgumentList.Add(string.Join(',', videoFilters));
            }
        }

        var pitch = Math.Pow(2, adjustments.PitchSemitones / 12);
        var audioFilters = new List<string>();

        if (Math.Abs(playbackSpeed - 1) > 0.001 || Math.Abs(pitch - 1) > 0.001)
        {
            audioFilters.Add(
                $"rubberband=tempo={playbackSpeed.ToString("0.###", CultureInfo.InvariantCulture)}:pitch={pitch.ToString("0.#####", CultureInfo.InvariantCulture)}");
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

        // Volume Boost
        if (adjustments.VolumeBoost > 1.01)
        {
            var boost = Math.Clamp(adjustments.VolumeBoost, 1.0, 3.0);
            audioFilters.Add($"volume={boost.ToString("0.##", CultureInfo.InvariantCulture)}");
            audioFilters.Add("alimiter=limit=0.98");
        }

        // Normalize
        if (adjustments.Normalize)
        {
            audioFilters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        if (audioFilters.Count > 0)
        {
            info.ArgumentList.Add("-af");
            info.ArgumentList.Add(string.Join(',', audioFilters));
        }
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