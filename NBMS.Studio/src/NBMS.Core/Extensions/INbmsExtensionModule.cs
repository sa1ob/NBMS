using NBMS.Core.Models;

namespace NBMS.Core.Extensions;

public interface INbmsExtensionModule
{
    string Id { get; }
    string Version { get; }
    IReadOnlyCollection<string> SupportedTimingTypes { get; }
    bool TryDescribeTimingEvent(TimingEvent timingEvent, out string description);
}

