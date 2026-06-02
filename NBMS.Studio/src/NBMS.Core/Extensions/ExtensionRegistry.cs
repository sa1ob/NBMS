using NBMS.Core.Models;

namespace NBMS.Core.Extensions;

public sealed class ExtensionRegistry
{
    private readonly List<INbmsExtensionModule> _modules = [];

    public IReadOnlyList<INbmsExtensionModule> Modules => _modules;

    public void Register(INbmsExtensionModule module)
    {
        if (_modules.Any(existing => existing.Id == module.Id))
        {
            return;
        }

        _modules.Add(module);
    }

    public bool TryDescribeTimingEvent(TimingEvent timingEvent, out string description)
    {
        foreach (var module in _modules)
        {
            if (!module.SupportedTimingTypes.Contains(timingEvent.Type))
            {
                continue;
            }

            if (module.TryDescribeTimingEvent(timingEvent, out description))
            {
                return true;
            }
        }

        description = "";
        return false;
    }
}

