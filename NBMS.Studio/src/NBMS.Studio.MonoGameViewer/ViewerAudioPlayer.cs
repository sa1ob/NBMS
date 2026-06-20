using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NBMS.Core.Services;

namespace NBMS.Studio.MonoGameViewer;

public sealed class ViewerAudioPlayer : IDisposable
{
    private const int MaxPreloadSeconds = 45;
    private const long MaxPreloadCacheBytes = 1536L * 1024L * 1024L;

    private readonly WaveOutEvent _output;
    private readonly MixingSampleProvider _mixer;
    private readonly WaveFormat _mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly List<IDisposable> _activeSources = [];
    private readonly Dictionary<string, PreloadedAudioClip> _preloadedClips = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nonPreloadableAudioIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _preloadFailureCountsByCodec = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ViewerAudioSettings _settings;
    private long _preloadedBytes;
    private long _usageSerial;

    public ViewerAudioPlayer(ViewerAudioSettings settings)
    {
        _settings = settings;
        _mixer = new MixingSampleProvider(_mixFormat)
        {
            ReadFully = true
        };
        _output = new WaveOutEvent
        {
            DesiredLatency = 60
        };
        _output.Init(new SimpleLimiterSampleProvider(_mixer, _settings.MasterGain, _settings.LimiterThreshold).ToWaveProvider());
        _output.Play();
    }

    public int Preload(IEnumerable<ViewerAudioFile> files, bool writePerformanceLog = true)
    {
        var fileList = files.ToList();
        using var measure = writePerformanceLog
            ? PerformanceLog.Measure($"ViewerAudioPlayer.Preload files={fileList.Count}")
            : null;
        var count = 0;
        long totalSamples = 0;
        foreach (var file in fileList)
        {
            if (Preload(file))
            {
                count++;
                lock (_gate)
                {
                    if (_preloadedClips.TryGetValue(file.AudioId, out var clip))
                    {
                        totalSamples += clip.Samples.Length;
                    }
                }
            }
        }

        long cacheBytes;
        lock (_gate)
        {
            cacheBytes = _preloadedBytes;
        }

        if (writePerformanceLog)
        {
            PerformanceLog.Mark($"ViewerAudioPlayer.Preload loaded={count} samples={totalSamples} approxBytes={totalSamples * sizeof(float)} cacheBytes={cacheBytes} failures={FormatPreloadFailureCounts()}");
        }

        return count;
    }

    public bool PreloadOne(ViewerAudioFile file)
    {
        return Preload(file);
    }

    public bool IsPreloaded(string audioId)
    {
        lock (_gate)
        {
            return _preloadedClips.ContainsKey(audioId);
        }
    }

    public bool PlayPreloadedOneShot(string audioId, double delaySeconds)
    {
        PreloadedAudioClip? clip;
        lock (_gate)
        {
            if (!_preloadedClips.TryGetValue(audioId, out clip))
            {
                return false;
            }

            clip.LastUsed = ++_usageSerial;
        }

        var source = new PreloadedSampleProvider(clip.Samples, clip.WaveFormat, RemoveDisposedSource);
        AddSampleProvider(source, new FadeInSampleProvider(source, 2.0), delaySeconds);
        return true;
    }

    public void PlayOneShot(string filePath, double delaySeconds)
    {
        WaveStream? reader = null;
        AutoDisposeSampleProvider? source = null;
        try
        {
            reader = CreateReader(filePath);
            var sample = NormalizeFormat(reader.ToSampleProvider());
            source = new AutoDisposeSampleProvider(sample, reader, RemoveDisposedSource);
            AddSampleProvider(source, new FadeInSampleProvider(source, 2.0), delaySeconds);
            source = null;
            reader = null;
        }
        catch
        {
            source?.Dispose();
            reader?.Dispose();
        }
    }

    private bool Preload(ViewerAudioFile file)
    {
        lock (_gate)
        {
            if (_preloadedClips.ContainsKey(file.AudioId) || _nonPreloadableAudioIds.Contains(file.AudioId))
            {
                return false;
            }
        }

        WaveStream? reader = null;
        try
        {
            reader = CreateReader(file.FilePath);
            var sample = NormalizeFormat(reader.ToSampleProvider());
            if (!TryReadAllSamples(sample, out var samples))
            {
                lock (_gate)
                {
                    _nonPreloadableAudioIds.Add(file.AudioId);
                    RegisterPreloadFailureLocked(file.FilePath);
                }

                return false;
            }

            var byteCount = samples.LongLength * sizeof(float);
            lock (_gate)
            {
                if (_preloadedClips.ContainsKey(file.AudioId) || _nonPreloadableAudioIds.Contains(file.AudioId))
                {
                    return false;
                }

                if (byteCount > MaxPreloadCacheBytes)
                {
                    _nonPreloadableAudioIds.Add(file.AudioId);
                    RegisterPreloadFailureLocked(file.FilePath);
                    return false;
                }

                EvictPreloadCacheFor(byteCount);
                _preloadedClips[file.AudioId] = new PreloadedAudioClip(samples, _mixFormat, byteCount, ++_usageSerial);
                _preloadedBytes += byteCount;
            }

            return true;
        }
        catch
        {
            lock (_gate)
            {
                _nonPreloadableAudioIds.Add(file.AudioId);
                RegisterPreloadFailureLocked(file.FilePath);
            }

            return false;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private void AddSampleProvider(IDisposable source, ISampleProvider sample, double delaySeconds)
    {
        var volume = new VolumeSampleProvider(sample)
        {
            Volume = _settings.AudioVolume
        };
        ISampleProvider scheduled = volume;
        var safeDelay = Math.Max(0, delaySeconds);
        if (safeDelay > 0.001)
        {
            scheduled = new OffsetSampleProvider(volume)
            {
                DelayBy = TimeSpan.FromSeconds(safeDelay)
            };
        }

        lock (_gate)
        {
            _activeSources.Add(source);
        }

        _mixer.AddMixerInput(scheduled);
    }

    public void StopActiveSounds()
    {
        List<IDisposable> sources;
        lock (_gate)
        {
            sources = _activeSources.ToList();
            _activeSources.Clear();
        }

        _mixer.RemoveAllMixerInputs();
        foreach (var source in sources)
        {
            source.Dispose();
        }
    }

    public void StopAll()
    {
        StopActiveSounds();
        lock (_gate)
        {
            _preloadedClips.Clear();
            _nonPreloadableAudioIds.Clear();
            _preloadFailureCountsByCodec.Clear();
            _preloadedBytes = 0;
        }
    }

    public void Dispose()
    {
        StopAll();
        _output.Stop();
        _output.Dispose();
    }

    private static WaveStream CreateReader(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ogg" or ".oga" => new VorbisWaveReader(filePath),
            _ => new MediaFoundationReader(filePath)
        };
    }

    private void RegisterPreloadFailureLocked(string filePath)
    {
        var codec = DetectCodecLabel(filePath);
        _preloadFailureCountsByCodec[codec] = _preloadFailureCountsByCodec.GetValueOrDefault(codec) + 1;
    }

    private string FormatPreloadFailureCounts()
    {
        lock (_gate)
        {
            return _preloadFailureCountsByCodec.Count == 0
                ? "none"
                : string.Join(",", _preloadFailureCountsByCodec
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}:{pair.Value}"));
        }
    }

    private static string DetectCodecLabel(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".wav" => "wav",
            ".flac" => "flac",
            ".ogg" or ".oga" => "ogg-vorbis",
            ".mp3" => "mp3",
            "" => "unknown",
            var extension => extension.TrimStart('.')
        };
    }

    private ISampleProvider NormalizeFormat(ISampleProvider source)
    {
        ISampleProvider result = source;
        if (result.WaveFormat.SampleRate != _mixFormat.SampleRate)
        {
            result = new WdlResamplingSampleProvider(result, _mixFormat.SampleRate);
        }

        result = result.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(result),
            2 => result,
            _ => new StereoFromMultiChannelSampleProvider(result)
        };

        return result;
    }

    private void RemoveDisposedSource(IDisposable source)
    {
        lock (_gate)
        {
            _activeSources.Remove(source);
        }
    }

    private void EvictPreloadCacheFor(long incomingBytes)
    {
        while (_preloadedBytes + incomingBytes > MaxPreloadCacheBytes && _preloadedClips.Count > 0)
        {
            var oldest = _preloadedClips
                .OrderBy(pair => pair.Value.LastUsed)
                .First();
            _preloadedBytes -= oldest.Value.ByteCount;
            _preloadedClips.Remove(oldest.Key);
        }
    }

    private static bool TryReadAllSamples(ISampleProvider source, out float[] samples)
    {
        var result = new List<float>();
        var maxSamples = source.WaveFormat.SampleRate * source.WaveFormat.Channels * MaxPreloadSeconds;
        var buffer = new float[source.WaveFormat.SampleRate * source.WaveFormat.Channels / 4];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            if (result.Count + read > maxSamples)
            {
                samples = [];
                return false;
            }

            for (var i = 0; i < read; i++)
            {
                result.Add(buffer[i]);
            }
        }

        samples = result.ToArray();
        return true;
    }
}

internal sealed class PreloadedSampleProvider : ISampleProvider, IDisposable
{
    private readonly float[] _samples;
    private readonly Action<IDisposable> _onDisposed;
    private int _position;
    private int _disposed;

    public PreloadedSampleProvider(float[] samples, WaveFormat waveFormat, Action<IDisposable> onDisposed)
    {
        _samples = samples;
        WaveFormat = waveFormat;
        _onDisposed = onDisposed;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        var available = _samples.Length - _position;
        var read = Math.Min(count, available);
        if (read > 0)
        {
            Array.Copy(_samples, _position, buffer, offset, read);
            _position += read;
        }

        if (read == 0 || _position >= _samples.Length)
        {
            Dispose();
        }

        return read;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _onDisposed(this);
    }
}

internal sealed class PreloadedAudioClip(float[] samples, WaveFormat waveFormat, long byteCount, long lastUsed)
{
    public float[] Samples { get; } = samples;

    public WaveFormat WaveFormat { get; } = waveFormat;

    public long ByteCount { get; } = byteCount;

    public long LastUsed { get; set; } = lastUsed;
}

public readonly record struct ViewerAudioSettings(float AudioVolume, float MasterGain, float LimiterThreshold);

internal sealed class AutoDisposeSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly IDisposable _disposable;
    private readonly Action<IDisposable> _onDisposed;
    private int _disposed;

    public AutoDisposeSampleProvider(ISampleProvider source, IDisposable disposable, Action<IDisposable> onDisposed)
    {
        _source = source;
        _disposable = disposable;
        _onDisposed = onDisposed;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        var read = _source.Read(buffer, offset, count);
        if (read == 0)
        {
            Dispose();
        }

        return read;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposable.Dispose();
        _onDisposed(this);
    }
}

internal sealed class FadeInSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _fadeInSamples;
    private int _position;

    public FadeInSampleProvider(ISampleProvider source, double fadeInMilliseconds)
    {
        _source = source;
        _fadeInSamples = Math.Max(0, (int)(source.WaveFormat.SampleRate * source.WaveFormat.Channels * fadeInMilliseconds / 1000.0));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (var i = 0; i < read && _position + i < _fadeInSamples; i++)
        {
            buffer[offset + i] *= (_position + i) / (float)Math.Max(1, _fadeInSamples);
        }

        _position += read;
        return read;
    }
}

internal sealed class StereoFromMultiChannelSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _sourceBuffer;

    public StereoFromMultiChannelSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceBuffer = new float[source.WaveFormat.Channels * 1024];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / 2;
        var sourceSamplesNeeded = Math.Min(_sourceBuffer.Length, framesRequested * _source.WaveFormat.Channels);
        var sourceRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        var framesRead = sourceRead / _source.WaveFormat.Channels;

        for (var frame = 0; frame < framesRead; frame++)
        {
            var sourceIndex = frame * _source.WaveFormat.Channels;
            buffer[offset + frame * 2] = _sourceBuffer[sourceIndex];
            buffer[offset + frame * 2 + 1] = _sourceBuffer[sourceIndex + 1];
        }

        return framesRead * 2;
    }
}

internal sealed class SimpleLimiterSampleProvider : ISampleProvider
{
    private const float Release = 0.0015f;
    private readonly ISampleProvider _source;
    private readonly float _masterGain;
    private readonly float _threshold;
    private float _gain = 1f;

    public SimpleLimiterSampleProvider(ISampleProvider source, float masterGain, float threshold)
    {
        _source = source;
        _masterGain = Math.Clamp(masterGain, 0f, 2f);
        _threshold = Math.Clamp(threshold, 0.1f, 1f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var index = offset + i;
            var value = buffer[index] * _masterGain;
            var absolute = MathF.Abs(value);
            var targetGain = absolute > _threshold ? _threshold / absolute : 1f;
            if (targetGain < _gain)
            {
                _gain = targetGain;
            }
            else
            {
                _gain = Math.Min(1f, _gain + Release);
            }

            buffer[index] = Math.Clamp(value * _gain, -_threshold, _threshold);
        }

        return read;
    }
}
