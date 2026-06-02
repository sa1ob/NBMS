using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NBMS.Studio.App.Playback;

public sealed class NbmsAudioPlayer : IDisposable
{
    private readonly WaveOutEvent _output;
    private readonly MixingSampleProvider _mixer;
    private readonly WaveFormat _mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly List<IDisposable> _activeSources = [];
    private readonly Queue<PlaybackRequest> _pendingRequests = [];
    private readonly SemaphoreSlim _requestSignal = new(0);
    private readonly object _gate = new();
    private readonly Task _workerTask;
    private bool _disposed;
    private int _generation;

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
        _output.Init(new SoftClipSampleProvider(_mixer).ToWaveProvider());
        _output.Play();
        _workerTask = Task.Run(ProcessPlaybackRequestsAsync);
    }

    public void PlayOneShot(string filePath)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingRequests.Enqueue(new PlaybackRequest(filePath, _generation));
        }

        Log($"queue {Path.GetFileName(filePath)}");
        _requestSignal.Release();
    }

    public void StopAll()
    {
        var pendingCount = 0;
        var activeCount = 0;
        lock (_gate)
        {
            _generation++;
            pendingCount = _pendingRequests.Count;
            activeCount = _activeSources.Count;
            _pendingRequests.Clear();
            _mixer.RemoveAllMixerInputs();

            foreach (var source in _activeSources.ToArray())
            {
                source.Dispose();
            }

            _activeSources.Clear();
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

            var volume = new VolumeSampleProvider(source)
            {
                Volume = 0.45f
            };

            lock (_gate)
            {
                if (_disposed || request.Generation != _generation)
                {
                    Log($"discard {Path.GetFileName(request.FilePath)}");
                    source.Dispose();
                    return;
                }

                _activeSources.Add(source);
                _mixer.AddMixerInput(volume);
                source = null;
                reader = null;
            }
        }
        catch (Exception ex)
        {
            Log($"failed {Path.GetFileName(request.FilePath)} {ex.GetType().Name}: {ex.Message}");
            source?.Dispose();
            reader?.Dispose();
        }
    }

    private static WaveStream CreateReader(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ogg" => new VorbisWaveReader(filePath),
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

    private readonly record struct PlaybackRequest(string FilePath, int Generation);

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
    private bool _disposed;

    public AutoDisposeSampleProvider(ISampleProvider source, IDisposable disposable, Action<IDisposable> onDisposed)
    {
        _source = source;
        _disposable = disposable;
        _onDisposed = onDisposed;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed)
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposable.Dispose();
        _onDisposed(this);
    }
}

internal sealed class SoftClipSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public SoftClipSampleProvider(ISampleProvider source)
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
            var value = buffer[index];
            if (MathF.Abs(value) > 1f)
            {
                buffer[index] = MathF.Sign(value) * (1f - 1f / (MathF.Abs(value) + 1f));
            }
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
