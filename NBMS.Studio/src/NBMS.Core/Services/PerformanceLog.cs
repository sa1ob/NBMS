using System.Diagnostics;

namespace NBMS.Core.Services;

public static class PerformanceLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "NBMS.Studio.Performance.log");

    public static IDisposable Measure(string name, double? minimumElapsedMs = null)
    {
        return new PerformanceScope(name, minimumElapsedMs);
    }

    public static void Mark(string message)
    {
        Write($"mark {message}");
    }

    private static void Write(string message)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // 計測ログは診断用なので、失敗しても本処理を止めない。
        }
    }

    private sealed class PerformanceScope : IDisposable
    {
        private readonly string _name;
        private readonly double? _minimumElapsedMs;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _disposed;

        public PerformanceScope(string name, double? minimumElapsedMs)
        {
            _name = name;
            _minimumElapsedMs = minimumElapsedMs;
            if (_minimumElapsedMs is null)
            {
                Write($"begin {_name}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopwatch.Stop();
            if (_minimumElapsedMs is { } minimumElapsedMs && _stopwatch.Elapsed.TotalMilliseconds < minimumElapsedMs)
            {
                return;
            }

            Write($"end {_name} elapsedMs={_stopwatch.Elapsed.TotalMilliseconds:0.###}");
        }
    }
}
