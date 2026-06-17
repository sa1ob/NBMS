using System.ComponentModel;
using System.Diagnostics;

namespace NBMS.Studio.MonoGameViewer;

internal sealed class FfmpegVideoDecoderFactory : IVideoBgaDecoderFactory
{
    private readonly string? _ffmpegPath;

    public FfmpegVideoDecoderFactory(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath;
    }

    public IVideoBgaDecoder Open(string sourcePath, TimeSpan? startOffset = null, bool startPaused = false)
    {
        return FfmpegVideoDecoder.Start(sourcePath, startOffset, ResolveFfmpegExecutable(_ffmpegPath), startPaused);
    }

    private static string ResolveFfmpegExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new InvalidOperationException($"configured ffmpeg was not found: {configuredPath}");
            }

            return configuredPath;
        }

        var environmentPath = Environment.GetEnvironmentVariable("NBMS_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            if (!File.Exists(environmentPath))
            {
                throw new InvalidOperationException($"NBMS_FFMPEG_PATH was set but not found: {environmentPath}");
            }

            return environmentPath;
        }

        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(bundledPath) ? bundledPath : "ffmpeg";
    }
}

internal sealed class FfmpegVideoDecoder : IVideoBgaDecoder
{
    public const int FrameWidth = 640;
    public const int FrameHeight = 360;

    private const int FramesPerSecond = 30;
    private const int FrameSize = FrameWidth * FrameHeight * 4;
    private const int MaxBufferedFrames = 90;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _frameLock = new();
    private readonly Queue<byte[]> _frameQueue = new();
    private readonly ManualResetEventSlim _firstFrameReady = new();
    private readonly Stopwatch _playbackClock = new();
    private readonly Task _readTask;
    private readonly Task _errorTask;
    private readonly Process _process;
    private readonly bool _startPaused;
    private byte[]? _latestFrame;
    private bool _hasNewFrame;
    private bool _isPlaying;
    private int _displayedFrameIndex;

    private FfmpegVideoDecoder(string sourcePath, Process process, bool startPaused)
    {
        SourcePath = sourcePath;
        _process = process;
        _startPaused = startPaused;
        _isPlaying = !startPaused;
        if (_isPlaying)
        {
            _playbackClock.Start();
        }

        _readTask = Task.Run(ReadFramesAsync);
        _errorTask = Task.Run(ReadErrorsAsync);
    }

    public string SourcePath { get; }

    public bool IsRunning
    {
        get
        {
            lock (_frameLock)
            {
                return string.IsNullOrWhiteSpace(LastError) &&
                       (!_process.HasExited || _frameQueue.Count > 0 || _latestFrame is not null);
            }
        }
    }

    public bool IsEnded
    {
        get
        {
            lock (_frameLock)
            {
                return _process.HasExited && _frameQueue.Count == 0 && string.IsNullOrWhiteSpace(LastError);
            }
        }
    }

    public bool SupportsPlaybackControl => true;

    public int OutputWidth => FrameWidth;

    public int OutputHeight => FrameHeight;

    public string LastError { get; private set; } = "";

    public static FfmpegVideoDecoder Start(
        string sourcePath,
        TimeSpan? startOffset = null,
        string ffmpegExecutable = "ffmpeg",
        bool startPaused = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        if (startOffset is { TotalSeconds: > 0.001 } offset)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(offset.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale={FrameWidth}:{FrameHeight}:force_original_aspect_ratio=decrease,pad={FrameWidth}:{FrameHeight}:(ow-iw)/2:(oh-ih)/2,fps={FramesPerSecond}");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("rgba");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ffmpeg process could not be started.");
            var decoder = new FfmpegVideoDecoder(sourcePath, process, startPaused);
            if (startPaused)
            {
                decoder._firstFrameReady.Wait(TimeSpan.FromSeconds(2));
            }

            return decoder;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("ffmpeg was not found. Set --ffmpeg, NBMS_FFMPEG_PATH, place ffmpeg.exe next to the Viewer, or add it to PATH.", ex);
        }
    }

    public byte[]? TakeLatestFrame()
    {
        lock (_frameLock)
        {
            if (!_hasNewFrame || _latestFrame is null)
            {
                return null;
            }

            _hasNewFrame = false;
            return (byte[])_latestFrame.Clone();
        }
    }

    public bool TryGetFrame(out VideoBgaFrame frame)
    {
        var bytes = TakeLatestFrame();
        if (bytes is null)
        {
            frame = new VideoBgaFrame([], FrameWidth, FrameHeight);
            return false;
        }

        frame = new VideoBgaFrame(bytes, FrameWidth, FrameHeight);
        return true;
    }

    public bool TryCopyFrame(byte[] destination)
    {
        return TryCopyFrame(destination, _playbackClock.Elapsed);
    }

    public bool TryCopyFrame(byte[] destination, TimeSpan presentationTime)
    {
        if (destination.Length < FrameSize)
        {
            throw new ArgumentException($"Frame buffer is too small. Required={FrameSize}", nameof(destination));
        }

        lock (_frameLock)
        {
            AdvanceFrameUnderLock(presentationTime);
            if (!_hasNewFrame || _latestFrame is null)
            {
                return false;
            }

            Buffer.BlockCopy(_latestFrame, 0, destination, 0, FrameSize);
            _hasNewFrame = false;
            return true;
        }
    }

    public bool Seek(TimeSpan position)
    {
        // ffmpeg pipe decoderは既存processのseekに未対応。Viewer側でoffset付き再openへfallbackする。
        return false;
    }

    public bool Play()
    {
        lock (_frameLock)
        {
            _isPlaying = true;
            _playbackClock.Restart();
            Monitor.PulseAll(_frameLock);
        }

        return true;
    }

    public bool Pause()
    {
        lock (_frameLock)
        {
            _isPlaying = false;
            _playbackClock.Stop();
        }

        return true;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        lock (_frameLock)
        {
            Monitor.PulseAll(_frameLock);
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _process.Dispose();
        _firstFrameReady.Dispose();
        _cancellation.Dispose();
    }

    private async Task ReadFramesAsync()
    {
        var buffer = new byte[FrameSize];
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var read = await ReadExactAsync(buffer, _cancellation.Token);
                if (read == 0)
                {
                    return;
                }

                if (read != FrameSize)
                {
                    LastError = $"short frame {read}/{FrameSize}";
                    return;
                }

                lock (_frameLock)
                {
                    while (_frameQueue.Count >= MaxBufferedFrames && !_cancellation.IsCancellationRequested)
                    {
                        Monitor.Wait(_frameLock, 100);
                    }

                    if (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    var frame = new byte[FrameSize];
                    Buffer.BlockCopy(buffer, 0, frame, 0, FrameSize);
                    if (_latestFrame is null)
                    {
                        _latestFrame = new byte[FrameSize];
                        Buffer.BlockCopy(frame, 0, _latestFrame, 0, FrameSize);
                        _hasNewFrame = true;
                        _firstFrameReady.Set();
                    }

                    _frameQueue.Enqueue(frame);
                    Monitor.PulseAll(_frameLock);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void AdvanceFrameUnderLock(TimeSpan presentationTime)
    {
        if (!_isPlaying)
        {
            return;
        }

        var targetFrameIndex = Math.Max(0, (int)Math.Floor(presentationTime.TotalSeconds * FramesPerSecond));
        while (_displayedFrameIndex <= targetFrameIndex && _frameQueue.Count > 0)
        {
            var frame = _frameQueue.Dequeue();
            _latestFrame ??= new byte[FrameSize];
            Buffer.BlockCopy(frame, 0, _latestFrame, 0, FrameSize);
            _hasNewFrame = true;
            _displayedFrameIndex++;
            Monitor.PulseAll(_frameLock);
        }
    }

    private async Task<int> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _process.StandardOutput.BaseStream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            var error = await _process.StandardError.ReadToEndAsync(_cancellation.Token);
            if (!string.IsNullOrWhiteSpace(error))
            {
                LastError = error.Trim().Split('\n').LastOrDefault()?.Trim() ?? error.Trim();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
