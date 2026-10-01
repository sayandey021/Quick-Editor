using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace QuickEditor;

public sealed class StereoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = new float[8192];

    public StereoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_sourceChannels == 2)
        {
            return _source.Read(buffer, offset, count);
        }

        int framesRequested = count / 2;
        int sourceSamplesNeeded = framesRequested * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / _sourceChannels;

        for (int i = 0; i < framesRead; i++)
        {
            int srcIdx = i * _sourceChannels;
            int dstIdx = offset + i * 2;
            if (_sourceChannels >= 6)
            {
                // 5.1 downmix: L, R, C, LFE, Ls, Rs
                float l = _sourceBuffer[srcIdx];
                float r = _sourceBuffer[srcIdx + 1];
                float c = _sourceBuffer[srcIdx + 2] * 0.707f;
                float ls = _sourceBuffer[srcIdx + 4] * 0.707f;
                float rs = _sourceBuffer[srcIdx + 5] * 0.707f;
                buffer[dstIdx] = Math.Clamp(l + c + ls, -1f, 1f);
                buffer[dstIdx + 1] = Math.Clamp(r + c + rs, -1f, 1f);
            }
            else
            {
                buffer[dstIdx] = _sourceBuffer[srcIdx];
                buffer[dstIdx + 1] = _sourceBuffer[srcIdx + 1];
            }
        }

        return framesRead * 2;
    }
}

public sealed class VarispeedSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly object _lock = new();
    private float[] _sourceBuffer = new float[16384];
    private int _sourceBufferCount;
    private double _position;

    public VarispeedSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public float PlaybackRate { get; set; } = 1.0f;

    public void Reset()
    {
        lock (_lock)
        {
            _sourceBufferCount = 0;
            _position = 0;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var rate = PlaybackRate;
        if (rate <= 0.01f)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        lock (_lock)
        {
            // Optimization for normal speed when buffer is empty
            if (Math.Abs(rate - 1.0f) < 0.001f && _position < 0.001 && _sourceBufferCount == 0)
            {
                return _source.Read(buffer, offset, count);
            }

            int framesRequested = count / _channels;
            int framesWritten = 0;

            while (framesWritten < framesRequested)
            {
                int currentFrame = (int)_position;
                if ((currentFrame + 2) * _channels > _sourceBufferCount)
                {
                    int framesToDiscard = currentFrame;
                    int samplesToDiscard = framesToDiscard * _channels;
                    int remainingSamples = _sourceBufferCount - samplesToDiscard;
                    if (remainingSamples > 0 && samplesToDiscard > 0)
                    {
                        Array.Copy(_sourceBuffer, samplesToDiscard, _sourceBuffer, 0, remainingSamples);
                    }
                    _sourceBufferCount = Math.Max(0, remainingSamples);
                    _position -= framesToDiscard;
                    currentFrame = (int)_position;

                    int readNeeded = _sourceBuffer.Length - _sourceBufferCount;
                    if (readNeeded > 0)
                    {
                        int read = _source.Read(_sourceBuffer, _sourceBufferCount, readNeeded);
                        _sourceBufferCount += read;
                        if (read == 0 && _sourceBufferCount <= (currentFrame + 1) * _channels)
                        {
                            break;
                        }
                    }
                }

                double frac = _position - currentFrame;
                int idx1 = currentFrame * _channels;
                int idx2 = (currentFrame + 1) * _channels;

                if (idx2 + _channels - 1 < _sourceBufferCount)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        float s1 = _sourceBuffer[idx1 + ch];
                        float s2 = _sourceBuffer[idx2 + ch];
                        buffer[offset + framesWritten * _channels + ch] = (float)((1.0 - frac) * s1 + frac * s2);
                    }
                    framesWritten++;
                    _position += rate;
                }
                else if (idx1 + _channels - 1 < _sourceBufferCount)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        buffer[offset + framesWritten * _channels + ch] = _sourceBuffer[idx1 + ch];
                    }
                    framesWritten++;
                    _position += rate;
                }
                else
                {
                    break;
                }
            }

            int samplesWritten = framesWritten * _channels;
            if (samplesWritten < count)
            {
                Array.Clear(buffer, offset + samplesWritten, count - samplesWritten);
            }
            return samplesWritten;
        }
    }
}

public sealed class BiquadPeakingFilter
{
    private float _a0 = 1f, _a1, _a2, _b1, _b2;
    private float _z1, _z2;

    public void SetParameters(float sampleRate, float centerFreq, float q, float dbGain)
    {
        if (Math.Abs(dbGain) < 0.05f)
        {
            _a0 = 1f; _a1 = 0f; _a2 = 0f; _b1 = 0f; _b2 = 0f;
            return;
        }

        double a = Math.Pow(10.0, dbGain / 40.0);
        double omega = 2.0 * Math.PI * Math.Clamp(centerFreq, 20.0, sampleRate * 0.48) / sampleRate;
        double sn = Math.Sin(omega);
        double cs = Math.Cos(omega);
        double alpha = sn / (2.0 * Math.Max(0.1, q));

        double b0Val = 1.0 + alpha * a;
        double b1Val = -2.0 * cs;
        double b2Val = 1.0 - alpha * a;
        double a0Val = 1.0 + alpha / a;
        double a1Val = -2.0 * cs;
        double a2Val = 1.0 - alpha / a;

        _a0 = (float)(b0Val / a0Val);
        _a1 = (float)(b1Val / a0Val);
        _a2 = (float)(b2Val / a0Val);
        _b1 = (float)(a1Val / a0Val);
        _b2 = (float)(a2Val / a0Val);
    }

    public void Reset()
    {
        _z1 = 0f;
        _z2 = 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float inSample)
    {
        float outSample = _a0 * inSample + _z1;
        _z1 = _a1 * inSample - _b1 * outSample + _z2;
        _z2 = _a2 * inSample - _b2 * outSample;
        return outSample;
    }
}

public sealed class EqualizerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _sampleRate;
    private readonly BiquadPeakingFilter[] _filtersL = new BiquadPeakingFilter[6];
    private readonly BiquadPeakingFilter[] _filtersR = new BiquadPeakingFilter[6];
    private static readonly double[] CenterFreqs = [80, 240, 750, 2200, 6000, 12000];
    private readonly double[] _gains = new double[6];
    private volatile bool _isBypassed = true;
    private readonly object _lock = new();

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;

        for (int i = 0; i < 6; i++)
        {
            _filtersL[i] = new BiquadPeakingFilter();
            _filtersR[i] = new BiquadPeakingFilter();
            _filtersL[i].SetParameters(_sampleRate, (float)CenterFreqs[i], 1.0f, 0f);
            _filtersR[i].SetParameters(_sampleRate, (float)CenterFreqs[i], 1.0f, 0f);
        }
    }

    public WaveFormat WaveFormat { get; }

    public void SetBands(double[] gains)
    {
        lock (_lock)
        {
            bool anyActive = false;
            for (int i = 0; i < 6 && i < gains.Length; i++)
            {
                _gains[i] = Math.Clamp(gains[i], -12.0, 12.0);
                if (Math.Abs(_gains[i]) > 0.05) anyActive = true;
                _filtersL[i].SetParameters(_sampleRate, (float)CenterFreqs[i], 1.0f, (float)_gains[i]);
                _filtersR[i].SetParameters(_sampleRate, (float)CenterFreqs[i], 1.0f, (float)_gains[i]);
            }
            _isBypassed = !anyActive;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            for (int i = 0; i < 6; i++)
            {
                _filtersL[i].Reset();
                _filtersR[i].Reset();
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (_isBypassed || read == 0) return read;

        lock (_lock)
        {
            if (_channels == 2)
            {
                for (int i = 0; i < read; i += 2)
                {
                    float l = buffer[offset + i];
                    float r = buffer[offset + i + 1];
                    for (int b = 0; b < 6; b++)
                    {
                        l = _filtersL[b].Process(l);
                        r = _filtersR[b].Process(r);
                    }
                    buffer[offset + i] = l;
                    buffer[offset + i + 1] = r;
                }
            }
            else
            {
                for (int i = 0; i < read; i++)
                {
                    float s = buffer[offset + i];
                    for (int b = 0; b < 6; b++)
                    {
                        s = _filtersL[b].Process(s);
                    }
                    buffer[offset + i] = s;
                }
            }
        }
        return read;
    }
}

public sealed class CombFilter
{
    private readonly float[] _buffer;
    private int _bufferIndex;
    private float _filterStore;

    public CombFilter(int size)
    {
        _buffer = new float[Math.Max(1, size)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input, float feedback, float damping)
    {
        float output = _buffer[_bufferIndex];
        _filterStore = output * (1f - damping) + _filterStore * damping;
        _buffer[_bufferIndex] = input + _filterStore * feedback;
        if (++_bufferIndex >= _buffer.Length) _bufferIndex = 0;
        return output;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _filterStore = 0f;
    }
}

public sealed class AllpassFilter
{
    private readonly float[] _buffer;
    private int _bufferIndex;

    public AllpassFilter(int size)
    {
        _buffer = new float[Math.Max(1, size)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input)
    {
        float bufOut = _buffer[_bufferIndex];
        float output = -input + bufOut;
        _buffer[_bufferIndex] = input + bufOut * 0.5f;
        if (++_bufferIndex >= _buffer.Length) _bufferIndex = 0;
        return output;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
    }
}

public sealed class ReverbSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private volatile float _wetMix;
    private volatile float _roomSize = 0.5f;
    private readonly CombFilter[] _combL;
    private readonly CombFilter[] _combR;
    private readonly AllpassFilter[] _allpassL;
    private readonly AllpassFilter[] _allpassR;
    private readonly object _lock = new();

    public ReverbSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = source.WaveFormat.Channels;
        float srRatio = source.WaveFormat.SampleRate / 44100.0f;

        int[] combDelaysL = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
        int[] combDelaysR = [1139, 1211, 1300, 1379, 1445, 1514, 1580, 1640];
        int[] allpassDelaysL = [556, 441, 341, 225];
        int[] allpassDelaysR = [579, 464, 364, 248];

        _combL = new CombFilter[combDelaysL.Length];
        _combR = new CombFilter[combDelaysR.Length];
        for (int i = 0; i < combDelaysL.Length; i++)
        {
            _combL[i] = new CombFilter((int)(combDelaysL[i] * srRatio));
            _combR[i] = new CombFilter((int)(combDelaysR[i] * srRatio));
        }

        _allpassL = new AllpassFilter[allpassDelaysL.Length];
        _allpassR = new AllpassFilter[allpassDelaysR.Length];
        for (int i = 0; i < allpassDelaysL.Length; i++)
        {
            _allpassL[i] = new AllpassFilter((int)(allpassDelaysL[i] * srRatio));
            _allpassR[i] = new AllpassFilter((int)(allpassDelaysR[i] * srRatio));
        }
    }

    public WaveFormat WaveFormat { get; }

    public void SetParameters(double wetMix, double roomSize)
    {
        lock (_lock)
        {
            _wetMix = (float)Math.Clamp(wetMix, 0.0, 1.0);
            _roomSize = (float)Math.Clamp(roomSize, 0.05, 0.98);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            foreach (var c in _combL) c.Clear();
            foreach (var c in _combR) c.Clear();
            foreach (var a in _allpassL) a.Clear();
            foreach (var a in _allpassR) a.Clear();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (_wetMix <= 0.001f || read == 0) return read;

        lock (_lock)
        {
            float wet = _wetMix * 0.45f;
            float dry = 1.0f - _wetMix * 0.35f;
            float feedback = 0.65f + _roomSize * 0.28f;
            float damping = 0.22f;

            if (_channels == 2)
            {
                for (int i = 0; i < read; i += 2)
                {
                    float inputL = buffer[offset + i];
                    float inputR = buffer[offset + i + 1];

                    float outL = 0f;
                    float outR = 0f;
                    for (int c = 0; c < _combL.Length; c++)
                    {
                        outL += _combL[c].Process(inputL, feedback, damping);
                        outR += _combR[c].Process(inputR, feedback, damping);
                    }

                    for (int a = 0; a < _allpassL.Length; a++)
                    {
                        outL = _allpassL[a].Process(outL);
                        outR = _allpassR[a].Process(outR);
                    }

                    buffer[offset + i] = inputL * dry + outL * wet;
                    buffer[offset + i + 1] = inputR * dry + outR * wet;
                }
            }
            else
            {
                for (int i = 0; i < read; i++)
                {
                    float input = buffer[offset + i];
                    float outL = 0f;
                    for (int c = 0; c < _combL.Length; c++)
                    {
                        outL += _combL[c].Process(input, feedback, damping);
                    }
                    for (int a = 0; a < _allpassL.Length; a++)
                    {
                        outL = _allpassL[a].Process(outL);
                    }
                    buffer[offset + i] = input * dry + outL * wet;
                }
            }
        }

        return read;
    }
}

public sealed class VolumeBoostAndNormalizeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _boost = 1.0f;
    private volatile bool _normalize;
    private volatile float _normGain = 1.0f;
    private readonly object _lock = new();

    public VolumeBoostAndNormalizeSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public void SetParameters(double volumeBoost, bool normalize, double measuredPeak)
    {
        lock (_lock)
        {
            _boost = (float)Math.Clamp(volumeBoost, 1.0, 3.0);
            _normalize = normalize;
            if (_normalize && measuredPeak > 0.01)
            {
                _normGain = (float)Math.Clamp(0.95 / measuredPeak, 0.5, 4.0);
            }
            else
            {
                _normGain = 1.0f;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        float factor;
        bool applyLimiter;

        lock (_lock)
        {
            factor = _boost * (_normalize ? _normGain : 1.0f);
            applyLimiter = factor > 1.001f || _normalize;
        }

        if (Math.Abs(factor - 1.0f) < 0.001f && !applyLimiter)
        {
            return read;
        }

        for (int i = 0; i < read; i++)
        {
            float s = buffer[offset + i] * factor;
            if (applyLimiter)
            {
                s = SoftLimit(s);
            }
            buffer[offset + i] = s;
        }

        return read;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftLimit(float sample)
    {
        if (sample > 0.95f)
        {
            float excess = sample - 0.95f;
            return 0.95f + (float)Math.Tanh(excess * 1.5) * 0.048f;
        }
        if (sample < -0.95f)
        {
            float excess = -sample - 0.95f;
            return -(0.95f + (float)Math.Tanh(excess * 1.5) * 0.048f);
        }
        return sample;
    }
}

public sealed class BypassablePitchShifterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SmbPitchShiftingSampleProvider _shifter;
    private volatile float _pitchFactor = 1.0f;

    public BypassablePitchShifterSampleProvider(ISampleProvider source, int fftSize, int osamp)
    {
        _source = source;
        _shifter = new SmbPitchShiftingSampleProvider(source, fftSize, osamp, 1.0f);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public float PitchFactor
    {
        get => _pitchFactor;
        set
        {
            _pitchFactor = value;
            _shifter.PitchFactor = value;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // When pitch is unshifted (factor ~1.0 / 0 semitones), bypass FFT phase vocoder completely
        if (Math.Abs(_pitchFactor - 1.0f) < 0.005f)
        {
            return _source.Read(buffer, offset, count);
        }

        return _shifter.Read(buffer, offset, count);
    }
}

public sealed class AudioPitchEngine : IDisposable
{
    private MediaFoundationReader? _reader;
    private VarispeedSampleProvider? _varispeed;
    private BypassablePitchShifterSampleProvider? _pitchShifter;
    private EqualizerSampleProvider? _equalizer;
    private ReverbSampleProvider? _reverb;
    private VolumeBoostAndNormalizeSampleProvider? _boostAndNormalize;
    private VolumeSampleProvider? _volumeProvider;
    private IWavePlayer? _outputDevice;
    private MediaFoundationResampler? _resampler;
    private readonly object _lock = new();
    private bool _isDisposed;
    private double _currentSpeed = 1.0;
    private double _currentSemitones = 0.0;
    private double _currentVolumeBoost = 1.0;
    private bool _currentNormalize = false;
    private double _measuredPeak = 1.0;
    private readonly double[] _currentEqBands = new double[6];
    private double _currentReverbMix = 0.0;
    private double _currentReverbRoomSize = 0.5;
    private float _volume = 1.0f;
    private bool _isMuted;

    public bool HasAudioTrack => _reader is not null;
    public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;

    public TimeSpan CurrentPosition
    {
        get
        {
            lock (_lock)
            {
                return _reader?.CurrentTime ?? TimeSpan.Zero;
            }
        }
    }

    public bool HasActiveAudioEffects
    {
        get
        {
            lock (_lock)
            {
                if (Math.Abs(_currentSemitones) > 0.01) return true;
                if (Math.Abs(_currentSpeed - 1.0) > 0.01) return true;
                if (_currentVolumeBoost > 1.01) return true;
                if (_currentNormalize) return true;
                if (_currentReverbMix > 0.01) return true;
                for (int i = 0; i < 6; i++)
                {
                    if (Math.Abs(_currentEqBands[i]) > 0.05) return true;
                }
                return false;
            }
        }
    }

    public bool Load(string filePath)
    {
        DisposeEngine();
        if (!File.Exists(filePath)) return false;

        try
        {
            lock (_lock)
            {
                _reader = new MediaFoundationReader(filePath);
                var sampleProvider = _reader.ToSampleProvider();

                // Ensure exactly 2 channels (stereo)
                if (sampleProvider.WaveFormat.Channels == 1)
                {
                    sampleProvider = sampleProvider.ToStereo();
                }
                else if (sampleProvider.WaveFormat.Channels > 2)
                {
                    sampleProvider = new StereoDownmixSampleProvider(sampleProvider);
                }

                _varispeed = new VarispeedSampleProvider(sampleProvider);
                // 2048 FFT size with 4x oversampling provides low ~40ms buffer latency for responsive real-time pitch shifting
                _pitchShifter = new BypassablePitchShifterSampleProvider(_varispeed, 2048, 4);
                _equalizer = new EqualizerSampleProvider(_pitchShifter);
                _equalizer.SetBands(_currentEqBands);
                _reverb = new ReverbSampleProvider(_equalizer);
                _reverb.SetParameters(_currentReverbMix, _currentReverbRoomSize);
                _boostAndNormalize = new VolumeBoostAndNormalizeSampleProvider(_reverb);
                _boostAndNormalize.SetParameters(_currentVolumeBoost, _currentNormalize, _measuredPeak);

                _volumeProvider = new VolumeSampleProvider(_boostAndNormalize)
                {
                    Volume = _isMuted ? 0f : _volume
                };

                var waveProvider = _volumeProvider.ToWaveProvider16();
                try
                {
                    // WASAPI Shared mode targets the user's active default audio endpoint (USB/Bluetooth/Speakers)
                    var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    var defaultDevice = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    var mixFormat = defaultDevice.AudioClient.MixFormat;
                    var wasapi = new WasapiOut(defaultDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 100);

                    IWaveProvider providerToPlay = waveProvider;
                    if (waveProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                    {
                        var resampler = new MediaFoundationResampler(waveProvider, new WaveFormat(mixFormat.SampleRate, 2))
                        {
                            ResamplerQuality = 60 // High quality
                        };
                        providerToPlay = resampler;
                        _resampler = resampler;
                    }

                    wasapi.Init(providerToPlay);
                    _outputDevice = wasapi;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WASAPI Init failed: {ex}");
                    // Fallback to WaveOutEvent if WASAPI is unavailable
                    var waveOut = new WaveOutEvent
                    {
                        DesiredLatency = 100,
                        NumberOfBuffers = 3
                    };
                    waveOut.Init(waveProvider);
                    _outputDevice = waveOut;
                }

                ApplyPitchAndSpeedInternal();
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioPitchEngine.Load failed: {ex}");
            DisposeEngine();
            return false;
        }
    }

    public void SetPitchAndSpeed(double semitones, double speed)
    {
        lock (_lock)
        {
            _currentSemitones = semitones;
            _currentSpeed = Math.Clamp(speed, 0.5, 2.0);
            ApplyPitchAndSpeedInternal();
        }
    }

    public void SetEqualizer(double[] bands)
    {
        lock (_lock)
        {
            if (bands is not null)
            {
                Array.Copy(bands, _currentEqBands, Math.Min(bands.Length, 6));
            }
            _equalizer?.SetBands(_currentEqBands);
        }
    }

    public void SetReverb(double mix, double roomSize)
    {
        lock (_lock)
        {
            _currentReverbMix = mix;
            _currentReverbRoomSize = roomSize;
            _reverb?.SetParameters(_currentReverbMix, _currentReverbRoomSize);
        }
    }

    public void SetVolumeBoostAndNormalize(double boost, bool normalize, double measuredPeak = 1.0)
    {
        lock (_lock)
        {
            _currentVolumeBoost = boost;
            _currentNormalize = normalize;
            if (measuredPeak > 0.01) _measuredPeak = measuredPeak;
            _boostAndNormalize?.SetParameters(_currentVolumeBoost, _currentNormalize, _measuredPeak);
        }
    }

    private void ApplyPitchAndSpeedInternal()
    {
        if (_varispeed is null || _pitchShifter is null) return;

        var s = (float)_currentSpeed;
        _varispeed.PlaybackRate = s;

        // Desired musical pitch shift factor: 2^(semitones / 12)
        var desiredPitch = (float)Math.Pow(2.0, _currentSemitones / 12.0);
        // Varispeed changes pitch by factor s, so pitch shifter compensates to yield desired pitch
        _pitchShifter.PitchFactor = Math.Clamp(desiredPitch / s, 0.25f, 4.0f);
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_outputDevice is null || _isDisposed) return;
            try
            {
                if (_outputDevice.PlaybackState != PlaybackState.Playing)
                {
                    _outputDevice.Play();
                }
            }
            catch { }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_outputDevice is null || _isDisposed) return;
            try
            {
                if (_outputDevice.PlaybackState == PlaybackState.Playing)
                {
                    _outputDevice.Pause();
                }
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_outputDevice is null || _isDisposed) return;
            try
            {
                _outputDevice.Stop();
            }
            catch { }
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_reader is null) return;
            try
            {
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (position > _reader.TotalTime) position = _reader.TotalTime;
                _reader.CurrentTime = position;
                _varispeed?.Reset();
                _equalizer?.Reset();
                _reverb?.Reset();
                _resampler?.Reposition();
            }
            catch { }
        }
    }

    public void SetVolume(double volume, bool isMuted)
    {
        lock (_lock)
        {
            _volume = (float)Math.Clamp(volume, 0.0, 1.0);
            _isMuted = isMuted;
            if (_volumeProvider is not null)
            {
                _volumeProvider.Volume = _isMuted ? 0f : _volume;
            }
        }
    }

    private void DisposeEngine()
    {
        lock (_lock)
        {
            try
            {
                _outputDevice?.Stop();
                _outputDevice?.Dispose();
            }
            catch { }
            _outputDevice = null;

            try
            {
                _resampler?.Dispose();
            }
            catch { }
            _resampler = null;

            try
            {
                _reader?.Dispose();
            }
            catch { }
            _reader = null;
            _varispeed = null;
            _pitchShifter = null;
            _equalizer = null;
            _reverb = null;
            _boostAndNormalize = null;
            _volumeProvider = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        DisposeEngine();
    }
}
