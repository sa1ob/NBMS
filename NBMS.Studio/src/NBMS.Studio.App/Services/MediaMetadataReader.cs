using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;

namespace NBMS.Studio.App.Services;

public sealed record MediaMetadata(int? Width, int? Height, int? DurationMs);

public static class MediaMetadataReader
{
    public static MediaMetadata TryRead(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new MediaMetadata(null, null, null);
        }

        if (IsImage(filePath))
        {
            return ReadImage(filePath);
        }

        if (IsVideo(filePath))
        {
            return new MediaMetadata(null, null, TryReadVideoDurationMs(filePath));
        }

        return new MediaMetadata(null, null, null);
    }

    private static MediaMetadata ReadImage(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var bitmap = new Bitmap(stream);
            return new MediaMetadata(
                Math.Max(0, bitmap.PixelSize.Width),
                Math.Max(0, bitmap.PixelSize.Height),
                null);
        }
        catch
        {
            return new MediaMetadata(null, null, null);
        }
    }

    private static int? TryReadVideoDurationMs(string filePath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode == 0 &&
                double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
            {
                return (int)Math.Round(seconds * 1000.0);
            }
        }
        catch
        {
            // ffprobe is optional; leave duration empty when it is not available.
        }

        return null;
    }

    private static bool IsImage(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

    private static bool IsVideo(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() is ".mp4" or ".avi" or ".webm" or ".mov" or ".mkv" or ".wmv";
    }
}
