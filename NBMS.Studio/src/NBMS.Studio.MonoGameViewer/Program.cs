namespace NBMS.Studio.MonoGameViewer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            AppendStartupLog($"start cwd={Environment.CurrentDirectory} args={string.Join(" ", args.Select(EscapeLogValue))}");
            var options = ViewerOptions.Parse(args);
            AppendStartupLog($"parsed header={options.HeaderPath ?? "<null>"} chart={options.ChartId ?? "<null>"} startTick={options.StartTick?.ToString() ?? "<null>"} endTick={options.EndTick?.ToString() ?? "<null>"} ffmpeg={options.FfmpegPath ?? "<auto>"} noBga={options.NoBga} videoLead={options.VideoLeadSeconds:0.000}s audioVolume={options.AudioVolume:0.00} masterGain={options.MasterGain:0.00} limiter={options.LimiterThreshold:0.00}");

            using var game = new ViewerGame(options);
            AppendStartupLog("game created");
            game.Run();
            AppendStartupLog("game exited");
        }
        catch (Exception ex)
        {
            AppendStartupLog(ex.ToString());
            throw;
        }
    }

    private static void AppendStartupLog(string message)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "NBMS.Studio.MonoGameViewer.log");
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }

    private static string EscapeLogValue(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }
}
