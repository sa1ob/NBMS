using System.Diagnostics;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NBMS.Studio.App.Playback;

public sealed class NbmsAudioPlayer : IDisposable
{
    private const float OneShotVolume = 0.22f;
    private const double OneShotFadeInMilliseconds = 3.0;
    private const int MaxPreloadSeconds = 30;

    private readonly WaveOutEvent _output;
    private readonly MixingSampleProvider _mixer;
    private readonly SampleCountingProvider _sampleCounter;
    private readonly WaveFormat _mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly List<IDisposable> _activeSources = [];
    private readonly Queue<PlaybackRequest> _pendingRequests = [];
    private readonly Dictionary<string, PreloadedAudioClip> _preloadedClips = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requestSignal = new(0);
    private readonly object _gate = new();
    private readonly Task _workerTask;
    private bool _disposed;
    private int _generation;
    private long _clockStartSampleFrame;
    private double _clockStartSeconds;
    private int _isPlaybackClockRunning;

    public event Action<string>? LogMessage;

    public NbmsAudioPlayer()
    {
        _mixer = new MixingSampleProvider(_mixFormat)
        {
            ReadFully = true
        };

        _output = new WaveOutEvent
        {
            DesiredLatency = 80
        };
        _sampleCounter = new SampleCountingProvider(new SimpleLimiterSampleProvider(_mixer));
        _output.Init(_sampleCounter.ToWaveProvider());
        _output.Play();
        _workerTask = Task.Run(ProcessPlaybackRequestsAsync);
    }

    public void StartPlaybackClock(double startSeconds)
    {
        Volatile.Write(ref _clockStartSampleFrame, _sampleCounter.TotalSampleFramesRead);
        _clockStartSeconds = startSeconds;
        Volatile.Write(ref _isPlaybackClockRunning, 1);
    }

    public double GetPlaybackClockSeconds()
    {
        if (Volatile.Read(ref _isPlaybackClockRunning) == 0)
        {
            return _clockStartSeconds;
        }

        var frames = _sampleCounter.TotalSampleFramesRead - Volatile.Read(ref _clockStartSampleFrame);
        return _clockStartSeconds + Math.Max(0, frames) / (double)_mixFormat.SampleRate;
    }

    public void StopPlaybackClock(double stopSeconds)
    {
        _clockStartSeconds = stopSeconds;
        Volatile.Write(ref _clockStartSampleFrame, _sampleCounter.TotalSampleFramesRead);
        Volatile.Write(ref _isPlaybackClockRunning, 0);
    }

    public void PlayOneShot(string filePath)
    {
        PlayOneShot(filePath, delaySeconds: 0);
    }

    public void PlayOneShot(string filePath, double delaySeconds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingRequests.Enqueue(new PlaybackRequest(
                filePath,
                _generation,
                Math.Max(0, delaySeconds),
                Stopwatch.GetTimestamp()));
        }

        Log($"queue {Path.GetFileName(filePath)} delay={Math.Max(0, delaySeconds):0.000}s");
        _requestSignal.Release();
    }

    public void Preload(IEnumerable<PlaybackPreloadRequest> requests)
    {
        foreach (var request in requests)
        {
            Preload(request);
        }
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
        PreloadedAudioClip clip;
        lock (_gate)
        {
            if (_disposed || !_preloadedClips.TryGetValue(audioId, out clip))
            {
                return false;
            }
        }

        var source = new PreloadedSampleProvider(clip.Samples, clip.WaveFormat, RemoveDisposedSource);
        var fadeIn = new FadeInSampleProvider(source, OneShotFadeInMilliseconds);
        var volume = new VolumeSampleProvider(fadeIn)
        {
            Volume = OneShotVolume
        };
        ISampleProvider scheduledSample = volume;
        var safeDelaySeconds = Math.Max(0, delaySeconds);
        if (safeDelaySeconds > 0.001)
        {
            scheduledSample = new OffsetSampleProvider(volume)
            {
                DelayBy = TimeSpan.FromSeconds(safeDelaySeconds)
            };
        }

        var shouldDispose = false;
        lock (_gate)
        {
            if (_disposed)
            {
                shouldDispose = true;
            }
            else
            {
                _activeSources.Add(source);
            }
        }

        if (shouldDispose)
        {
            source.Dispose();
            return false;
        }

        _mixer.AddMixerInput(scheduledSample);
        Log($"add-preloaded {audioId} delay={safeDelaySeconds:0.000}s");
        return true;
    }

    public void ClearPreloaded()
    {
        lock (_gate)
        {
            _preloadedClips.Clear();
        }
    }

    public void StopAll()
    {
        var pendingCount = 0;
        var activeCount = 0;
        List<IDisposable> sources;
        lock (_gate)
        {
            _generation++;
            pendingCount = _pendingRequests.Count;
            activeCount = _activeSources.Count;
            _pendingRequests.Clear();
            sources = _activeSources.ToList();
            _activeSources.Clear();
        }

        _mixer.RemoveAllMixerInputs();
        foreach (var source in sources)
        {
            source.Dispose();
        }

        if (pendingCount > 0 || activeCount > 0)
        {
            Log($"stop active={activeCount} pending={pendingCount}");
        }
    }

    private async Task ProcessPlaybackRequestsAsync()
    {
        while (true)
        {
            await _requestSignal.WaitAsync().ConfigureAwait(false);

            PlaybackRequest? request = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_pendingRequests.Count > 0)
                {
                    request = _pendingRequests.Dequeue();
                }
            }

            if (request is null)
            {
                continue;
            }

            AddOneShot(request.Value);
        }
    }

    private void AddOneShot(PlaybackRequest request)
    {
        WaveStream? reader = null;
        AutoDisposeSampleProvider? source = null;

        try
        {
            Log($"open {Path.GetFileName(request.FilePath)}");
            reader = CreateReader(request.FilePath);
            Log($"ready {Path.GetFileName(request.FilePath)} {reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch");
            var sample = NormalizeFormat(reader.ToSampleProvider());
            source = new AutoDisposeSampleProvider(sample, reader, RemoveDisposedSource);

            var fadeIn = new FadeInSampleProvider(source, OneShotFadeInMilliseconds);
            var volume = new VolumeSampleProvider(fadeIn)
            {
                Volume = OneShotVolume
            };
            var remainingDelaySeconds = ResolveRemainingDelaySeconds(request);
            ISampleProvider scheduledSample = volume;
            if (remainingDelaySeconds > 0.001)
            {
                scheduledSample = new OffsetSampleProvider(volume)
                {
                    DelayBy = TimeSpan.FromSeconds(remainingDelaySeconds)
                };
            }

            var shouldDiscard = false;
            lock (_gate)
            {
                if (_disposed || request.Generation != _generation)
                {
                    Log($"discard {Path.GetFileName(request.FilePath)}");
                    shouldDiscard = true;
                }
                else
                {
                    _activeSources.Add(source);
                }
            }

            if (shouldDiscard)
            {
                source.Dispose();
                return;
            }

            _mixer.AddMixerInput(scheduledSample);
            Log($"add {Path.GetFileName(request.FilePath)} remainingDelay={remainingDelaySeconds:0.000}s");
            source = null;
            reader = null;
        }
        catch (Exception ex)
        {
            Log($"failed {Path.GetFileName(request.FilePath)} {ex.GetType().Name}: {ex.Message}");
            source?.Dispose();
            reader?.Dispose();
        }
    }

    private void Preload(PlaybackPreloadRequest request)
    {
        lock (_gate)
        {
            if (_disposed || _preloadedClips.ContainsKey(request.AudioId))
            {
                return;
            }
        }

        WaveStream? reader = null;
        try
        {
            Log($"preload-open {request.AudioId} {Path.GetFileName(request.FilePath)}");
            reader = CreateReader(request.FilePath);
            var sample = NormalizeFormat(reader.ToSampleProvider());
            var samples = ReadAllSamples(sample);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _preloadedClips[request.AudioId] = new PreloadedAudioClip(samples, _mixFormat);
                    Log($"preload-ready {request.AudioId} samples={samples.Length}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"preload-failed {request.AudioId} {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private static WaveStream CreateReader(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ogg" or ".oga" => new VorbisWaveReader(filePath),
            _ => new MediaFoundationReader(filePath)
        };
    }

    private void RemoveDisposedSource(IDisposable source)
    {
        lock (_gate)
        {
            _activeSources.Remove(source);
        }
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

    private static float[] ReadAllSamples(ISampleProvider source)
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
                throw new InvalidOperationException($"preload limit exceeded ({MaxPreloadSeconds}s)");
            }

            for (var i = 0; i < read; i++)
            {
                result.Add(buffer[i]);
            }
        }

        return result.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _requestSignal.Release();
        StopAll();
        _workerTask.Wait(TimeSpan.FromSeconds(1));
        _output.Stop();
        _output.Dispose();
        _requestSignal.Dispose();
    }

    private static double ResolveRemainingDelaySeconds(PlaybackRequest request)
    {
        if (request.DelaySeconds <= 0)
        {
            return 0;
        }

        var elapsedSeconds = (Stopwatch.GetTimestamp() - request.EnqueuedAtTicks) / (double)Stopwatch.Frequency;
        return Math.Max(0, request.DelaySeconds - elapsedSeconds);
    }

    private readonly record struct PlaybackRequest(
        string FilePath,
        int Generation,
        double DelaySeconds,
        long EnqueuedAtTicks);

    private readonly record struct PreloadedAudioClip(float[] Samples, WaveFormat WaveFormat);

    private void Log(string message)
    {
        LogMessage?.Invoke(message);
    }
}

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
        if (_fadeInSamples <= 0)
        {
            _position += read;
            return read;
        }

        for (var i = 0; i < read; i++)
        {
            var fadePosition = _position + i;
            if (fadePosition >= _fadeInSamples)
            {
                break;
            }

            buffer[offset + i] *= fadePosition / (float)_fadeInSamples;
        }

        _position += read;
        return read;
    }
}

internal sealed class SimpleLimiterSampleProvider : ISampleProvider
{
    private const float MasterGain = 0.85f;
    private const float Threshold = 0.92f;
    private const float Release = 0.0015f;
    private readonly ISampleProvider _source;
    private float _gain = 1f;

    public SimpleLimiterSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var index = offset + i;
            var value = buffer[index] * MasterGain;
            var absolute = MathF.Abs(value);
            var targetGain = absolute > Threshold ? Threshold / absolute : 1f;
            if (targetGain < _gain)
            {
                _gain = targetGain;
            }
            else
            {
                _gain = Math.Min(1f, _gain + Release);
            }

            buffer[index] = Math.Clamp(value * _gain, -Threshold, Threshold);
        }

        return read;
    }
}

internal sealed class SampleCountingProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private long _totalSamplesRead;

    public SampleCountingProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public long TotalSampleFramesRead => Interlocked.Read(ref _totalSamplesRead) / WaveFormat.Channels;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            Interlocked.Add(ref _totalSamplesRead, read);
        }

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
        var sourceSamplesNeeded = framesRequested * _source.WaveFormat.Channels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
        {
            sourceSamplesNeeded = _sourceBuffer.Length;
        }

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
