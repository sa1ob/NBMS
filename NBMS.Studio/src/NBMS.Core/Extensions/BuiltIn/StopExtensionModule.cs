using NBMS.Core.Models;

namespace NBMS.Core.Extensions.BuiltIn;

public sealed class StopExtensionModule : INbmsExtensionModule
{
    public string Id => "nbms.stop";
    public string Version => "0.1.0";
    public IReadOnlyCollection<string> SupportedTimingTypes { get; } = ["stop"];

    public bool TryDescribeTimingEvent(TimingEvent timingEvent, out string description)
    {
        if (timingEvent.Type != "stop")
        {
            description = "";
            return false;
        }

        description = $"STOP {timingEvent.DurationTicks ?? 0} ticks";
        return true;
    }
}

