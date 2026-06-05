namespace NBMS.Studio.MonoGameViewer;

public sealed record ViewerOptions(string? HeaderPath, string? ChartId)
{
    public static ViewerOptions Parse(string[] args)
    {
        string? headerPath = null;
        string? chartId = null;

        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value.Equals("--chart", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                chartId = args[++index];
                continue;
            }

            if (!value.StartsWith("--", StringComparison.Ordinal) && headerPath is null)
            {
                headerPath = value;
            }
        }

        return new ViewerOptions(headerPath, chartId);
    }
}
