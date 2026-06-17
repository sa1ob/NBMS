namespace NBMS.Studio.MonoGameViewer;

public sealed record ViewerOptions(
    string? HeaderPath,
    string? ChartId,
    int? StartTick,
    int? EndTick,
    string? FfmpegPath,
    bool NoBga,
    double VideoLeadSeconds,
    float AudioVolume,
    float MasterGain,
    float LimiterThreshold)
{
    public const double DefaultVideoLeadSeconds = 0.36;
    public const float DefaultAudioVolume = 0.28f;
    public const float DefaultMasterGain = 0.82f;
    public const float DefaultLimiterThreshold = 0.90f;

    public static ViewerOptions Parse(string[] args)
    {
        string? headerPath = null;
        string? chartId = null;
        int? startTick = null;
        int? endTick = null;
        string? ffmpegPath = null;
        var videoLeadSeconds = DefaultVideoLeadSeconds;
        var audioVolume = DefaultAudioVolume;
        var masterGain = DefaultMasterGain;
        var limiterThreshold = DefaultLimiterThreshold;
        var noBga = false;

        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value.Equals("--chart", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                chartId = args[++index];
                continue;
            }

            if (value.Equals("--start-tick", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (int.TryParse(args[++index], out var parsedTick))
                {
                    startTick = Math.Max(0, parsedTick);
                }

                continue;
            }

            if (value.Equals("--end-tick", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (int.TryParse(args[++index], out var parsedTick))
                {
                    endTick = Math.Max(0, parsedTick);
                }

                continue;
            }

            if (value.Equals("--ffmpeg", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                ffmpegPath = args[++index];
                continue;
            }

            if (value.Equals("--video-lead-ms", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (double.TryParse(args[++index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedMs))
                {
                    videoLeadSeconds = Math.Clamp(parsedMs / 1000.0, 0, 1.5);
                }

                continue;
            }

            if (value.Equals("--audio-volume", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (float.TryParse(args[++index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedVolume))
                {
                    audioVolume = Math.Clamp(parsedVolume, 0f, 2f);
                }

                continue;
            }

            if (value.Equals("--master-gain", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (float.TryParse(args[++index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedGain))
                {
                    masterGain = Math.Clamp(parsedGain, 0f, 2f);
                }

                continue;
            }

            if (value.Equals("--limiter-threshold", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (float.TryParse(args[++index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedThreshold))
                {
                    limiterThreshold = Math.Clamp(parsedThreshold, 0.1f, 1f);
                }

                continue;
            }

            if (value.Equals("--no-bga", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("-nobga", StringComparison.OrdinalIgnoreCase))
            {
                noBga = true;
                continue;
            }

            if (!value.StartsWith("--", StringComparison.Ordinal) && headerPath is null)
            {
                headerPath = value;
            }
        }

        return new ViewerOptions(headerPath, chartId, startTick, endTick, ffmpegPath, noBga, videoLeadSeconds, audioVolume, masterGain, limiterThreshold);
    }
}
