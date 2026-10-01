using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace QuickEditor;

public sealed class WaveformService
{
    private const int SampleRate = 8000;
    private const int PeaksPerSecond = 30;
    private const int MaximumPeakCount = 12000;

    public async Task<IReadOnlyList<float>> CreatePeaksAsync(
        string inputPath,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var ffmpeg = FindExecutable("ffmpeg.exe");
        if (ffmpeg is null)
        {
            return Array.Empty<float>();
        }

        var peakCount = Math.Clamp((int)Math.Ceiling(durationSeconds * PeaksPerSecond), 256, MaximumPeakCount);
        var peaks = new float[peakCount];
        var expectedSamples = Math.Max(1, durationSeconds * SampleRate);
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(inputPath);
        info.ArgumentList.Add("-map");
        info.ArgumentList.Add("0:a:0?");
        info.ArgumentList.Add("-vn");
        info.ArgumentList.Add("-ac");
        info.ArgumentList.Add("1");
        info.ArgumentList.Add("-ar");
        info.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("s16le");
        info.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                return Array.Empty<float>();
            }

            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            long sampleIndex = 0;
            var pendingByte = -1;
            while (true)
            {
                var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                var offset = 0;
                if (pendingByte >= 0)
                {
                    var value = (short)(pendingByte | (buffer[0] << 8));
                    AddSample(peaks, expectedSamples, sampleIndex++, value);
                    pendingByte = -1;
                    offset = 1;
                }

                for (; offset + 1 < bytesRead; offset += 2)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2));
                    AddSample(peaks, expectedSamples, sampleIndex++, sample);
                }

                if (offset < bytesRead)
                {
                    pendingByte = buffer[offset];
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                return Array.Empty<float>();
            }

            return peaks;
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
    }

    private static void AddSample(float[] peaks, double expectedSamples, long sampleIndex, short sample)
    {
        var peakIndex = Math.Min(peaks.Length - 1, (int)(sampleIndex / expectedSamples * peaks.Length));
        peaks[peakIndex] = Math.Max(peaks[peakIndex], Math.Abs((int)sample) / 32768f);
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
