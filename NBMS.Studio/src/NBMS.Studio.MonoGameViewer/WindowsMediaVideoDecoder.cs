using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NBMS.Studio.MonoGameViewer;

internal sealed class WindowsMediaVideoDecoderFactory : IVideoBgaDecoderFactory
{
    public IVideoBgaDecoder Open(string sourcePath, TimeSpan? startOffset = null, bool startPaused = false)
    {
        return WindowsMediaVideoDecoder.Open(sourcePath, startOffset, startPaused);
    }
}

internal sealed class WindowsMediaVideoDecoder : IVideoBgaDecoder
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 360;
    private const int FrameStride = FrameWidth * 4;
    private const int FrameSize = FrameStride * FrameHeight;

    private readonly object _frameLock = new();
    private readonly ManualResetEventSlim _dispatcherReady = new();
    private readonly ManualResetEventSlim _openCompleted = new();
    private readonly ManualResetEventSlim _firstFrameReady = new();
    private readonly TimeSpan _startOffset;
    private readonly bool _startPaused;
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private byte[]? _latestFrame;
    private byte[]? _captureFrame;
    private bool _hasNewFrame;
    private volatile bool _playRequested;
    private bool _disposed;

    private WindowsMediaVideoDecoder(string sourcePath, TimeSpan? startOffset, bool startPaused)
    {
        SourcePath = sourcePath;
        _startOffset = startOffset ?? TimeSpan.Zero;
        _startPaused = startPaused;
        _thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "NBMS Windows Media BGA Decoder"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public string SourcePath { get; }

    public int OutputWidth => FrameWidth;

    public int OutputHeight => FrameHeight;

    public bool IsRunning { get; private set; }

    public bool IsEnded { get; private set; }

    public bool SupportsPlaybackControl => true;

    public string LastError { get; private set; } = "";

    public static WindowsMediaVideoDecoder Open(string sourcePath, TimeSpan? startOffset, bool startPaused = false)
    {
        var decoder = new WindowsMediaVideoDecoder(sourcePath, startOffset, startPaused);
        decoder._thread.Start();

        if (!decoder._dispatcherReady.Wait(TimeSpan.FromSeconds(2)))
        {
            decoder.Dispose();
            throw new InvalidOperationException("Windows media dispatcher did not start.");
        }

        if (!decoder._openCompleted.Wait(TimeSpan.FromSeconds(3)))
        {
            decoder.Dispose();
            throw new InvalidOperationException("Windows media decoder open timed out.");
        }

        if (startPaused)
        {
            decoder._firstFrameReady.Wait(TimeSpan.FromSeconds(2));
        }

        if (!string.IsNullOrWhiteSpace(decoder.LastError))
        {
            var error = decoder.LastError;
            decoder.Dispose();
            throw new InvalidOperationException(error);
        }

        return decoder;
    }

    public bool TryGetFrame(out VideoBgaFrame frame)
    {
        lock (_frameLock)
        {
            if (!_hasNewFrame || _latestFrame is null)
            {
                frame = new VideoBgaFrame([], FrameWidth, FrameHeight);
                return false;
            }

            _hasNewFrame = false;
            frame = new VideoBgaFrame((byte[])_latestFrame.Clone(), FrameWidth, FrameHeight);
            return true;
        }
    }

    public bool TryCopyFrame(byte[] destination)
    {
        return TryCopyFrame(destination, TimeSpan.Zero);
    }

    public bool TryCopyFrame(byte[] destination, TimeSpan presentationTime)
    {
        if (destination.Length < FrameSize)
        {
            throw new ArgumentException($"Frame buffer is too small. Required={FrameSize}", nameof(destination));
        }

        lock (_frameLock)
        {
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
        var dispatcher = _dispatcher;
        var player = _player;
        if (dispatcher is null || player is null || _disposed)
        {
            return false;
        }

        try
        {
            _playRequested = true;
            dispatcher.BeginInvoke(() =>
            {
                player.Position = position;
                if (!IsRunning)
                {
                    IsRunning = true;
                    IsEnded = false;
                    player.Play();
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool Play()
    {
        var dispatcher = _dispatcher;
        var player = _player;
        if (dispatcher is null || player is null || _disposed)
        {
            return false;
        }

        try
        {
            _playRequested = true;
            IsRunning = true;
            IsEnded = false;
            dispatcher.BeginInvoke(() =>
            {
                IsRunning = true;
                IsEnded = false;
                player.Play();
            });
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool Pause()
    {
        var dispatcher = _dispatcher;
        var player = _player;
        if (dispatcher is null || player is null || _disposed)
        {
            return false;
        }

        try
        {
            _playRequested = false;
            dispatcher.BeginInvoke(() =>
            {
                player.Pause();
                IsRunning = false;
            });
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _dispatcher?.BeginInvoke(() =>
            {
                _timer?.Stop();
                _player?.Close();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            });
            if (_thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
        }

        _dispatcherReady.Dispose();
        _openCompleted.Dispose();
        _firstFrameReady.Dispose();
    }

    private void RunDispatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            _player = new MediaPlayer
            {
                Volume = 0,
                ScrubbingEnabled = true
            };
            _player.MediaOpened += HandleMediaOpened;
            _player.MediaFailed += (_, args) =>
            {
                LastError = args.ErrorException.Message;
                IsRunning = false;
                _openCompleted.Set();
            };
            _player.MediaEnded += (_, _) =>
            {
                IsEnded = true;
                IsRunning = false;
            };

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromSeconds(1.0 / 30.0)
            };
            _timer.Tick += (_, _) => CaptureFrame();
            _timer.Start();

            _dispatcherReady.Set();
            _player.Open(new Uri(SourcePath, UriKind.Absolute));
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _dispatcherReady.Set();
            _openCompleted.Set();
        }
    }

    private void HandleMediaOpened(object? sender, EventArgs args)
    {
        try
        {
            if (_startOffset > TimeSpan.Zero)
            {
                _player!.Position = _startOffset;
            }

            IsEnded = false;
            if (_startPaused)
            {
                IsRunning = false;
                _playRequested = false;
                _player!.Play();
                StartPausedWarmup();
            }
            else
            {
                IsRunning = true;
                _player!.Play();
            }
            _openCompleted.Set();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _openCompleted.Set();
        }
    }

    private void StartPausedWarmup()
    {
        if (_player is null)
        {
            return;
        }

        var warmup = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        warmup.Tick += (_, _) =>
        {
            warmup.Stop();
            try
            {
                CaptureFrame();
                if (_playRequested)
                {
                    IsRunning = true;
                }
                else
                {
                    _player.Pause();
                    if (_startOffset > TimeSpan.Zero)
                    {
                        _player.Position = _startOffset;
                    }
                    else
                    {
                        _player.Position = TimeSpan.Zero;
                    }

                    IsRunning = false;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        };
        warmup.Start();
    }

    private void CaptureFrame()
    {
        if (_player is null || !string.IsNullOrWhiteSpace(LastError))
        {
            return;
        }

        try
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, FrameWidth, FrameHeight));
                context.DrawVideo(_player, new Rect(0, 0, FrameWidth, FrameHeight));
            }

            var bitmap = new RenderTargetBitmap(FrameWidth, FrameHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = _captureFrame ??= new byte[FrameSize];
            bitmap.CopyPixels(pixels, FrameStride, 0);
            ConvertBgraToRgba(pixels);

            lock (_frameLock)
            {
                _latestFrame ??= new byte[FrameSize];
                Buffer.BlockCopy(pixels, 0, _latestFrame, 0, FrameSize);
                _hasNewFrame = true;
                _firstFrameReady.Set();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
        }
    }

    private static void ConvertBgraToRgba(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }
    }
}
