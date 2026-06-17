using NAudio.Vorbis;
using NAudio.Wave;

namespace NBMS.Studio.App.Services;

public sealed record AudioMetadata(int DurationMs, int SampleRate, int Channels);

public static class AudioMetadataReader
{
    public static AudioMetadata? TryRead(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var reader = CreateReader(filePath);
            return new AudioMetadata(
                Math.Max(0, (int)Math.Round(reader.TotalTime.TotalMilliseconds)),
                Math.Max(0, reader.WaveFormat.SampleRate),
                Math.Max(0, reader.WaveFormat.Channels));
        }
        catch
        {
            return null;
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
}
