using System.ComponentModel;
using ModelContextProtocol.Server;
using Ocdup.Config;

namespace Ocdup.Tools;

[McpServerToolType]
public static class ConfigTools
{
    [McpServerTool(Name = "show_config")]
    [Description("Show the effective Ocdup settings (probe/target configs, command port, permissions) and where each value comes from: machine defaults, ocd.json, or session changes. Use this to answer \"why is this setting in effect?\". Set reload=true to re-read ocd.json from disk after editing it.")]
    public static string ShowConfig(
        [Description("Re-read ocd.json from disk instead of using the snapshot from startup (default false).")] bool reload = false)
    {
        var store = ConfigStore.Instance;
        if (reload) store.Reload();
        return store.Current.Describe();
    }
}

