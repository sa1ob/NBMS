using NBMS.Core.Models;

namespace NBMS.Core.Extensions.BuiltIn;

public sealed class ScrollExtensionModule : INbmsExtensionModule
{
    public string Id => "nbms.scroll";
    public string Version => "0.1.0";
    public IReadOnlyCollection<string> SupportedTimingTypes { get; } = ["scroll"];

    public bool TryDescribeTimingEvent(TimingEvent timingEvent, out string description)
    {
        if (timingEvent.Type != "scroll")
        {
            description = "";
            return false;
        }

        description = $"SCROLL {timingEvent.Value ?? 1.0}";
        return true;
    }
}

