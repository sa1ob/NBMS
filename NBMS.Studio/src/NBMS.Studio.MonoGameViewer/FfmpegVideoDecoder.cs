using System.ComponentModel;
using System.Diagnostics;

namespace NBMS.Studio.MonoGameViewer;

internal sealed class FfmpegVideoDecoder : IDisposable
{
    public const int OutputWidth = 640;
    public const int OutputHeight = 360;

    private const int FramesPerSecond = 30;
    private const int FrameSize = OutputWidth * OutputHeight * 4;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _frameLock = new();
    private readonly Task _readTask;
    private readonly Task _errorTask;
    private readonly Process _process;
    private byte[]? _latestFrame;
    private bool _hasNewFrame;

    private FfmpegVideoDecoder(string sourcePath, Process process)
    {
        SourcePath = sourcePath;
        _process = process;
        _readTask = Task.Run(ReadFramesAsync);
        _errorTask = Task.Run(ReadErrorsAsync);
    }

    public string SourcePath { get; }

    public bool IsRunning => !_process.HasExited && string.IsNullOrWhiteSpace(LastError);

    public string LastError { get; private set; } = "";

    public static FfmpegVideoDecoder Start(string sourcePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-re");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale={OutputWidth}:{OutputHeight}:force_original_aspect_ratio=decrease,pad={OutputWidth}:{OutputHeight}:(ow-iw)/2:(oh-ih)/2,fps={FramesPerSecond}");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("rgba");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ffmpeg process could not be started.");
            return new FfmpegVideoDecoder(sourcePath, process);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("ffmpeg was not found. Install ffmpeg and add it to PATH.", ex);
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

    public void Dispose()
    {
        _cancellation.Cancel();
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
                    _latestFrame = (byte[])buffer.Clone();
                    _hasNewFrame = true;
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
