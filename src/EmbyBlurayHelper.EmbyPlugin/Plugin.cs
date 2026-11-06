using System.Runtime.InteropServices;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

[assembly: Guid("cbe8e7e0-9155-48e9-8f45-912b7062c447")]

namespace EmbyBlurayHelper.EmbyPlugin;

public sealed class Plugin : BasePlugin<HelperPluginConfiguration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    internal static readonly SemaphoreSlim ProbeGate = new(1, 1);
    internal Updates.PluginUpdater Updater { get; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        Updater = new Updates.PluginUpdater(paths.PluginsPath, typeof(Plugin).Assembly.GetName().Version!);
    }
    public override string Name => "EmbyBlurayHelper";

    public override string Description => "Provides explicit Blu-ray media probing for ISO and BDMV sources.";
    public IEnumerable<PluginPageInfo> GetPages() => new[] {
        new PluginPageInfo { Name = "EmbyBlurayHelperUpdate", DisplayName = "EmbyBlurayHelper", IsMainConfigPage = true,
            EmbeddedResourcePath = "EmbyBlurayHelper.Plugin.update.html" },
        new PluginPageInfo { Name = "EmbyBlurayHelperUpdateJs", EmbeddedResourcePath = "EmbyBlurayHelper.Plugin.update.js" }
    };
}

public sealed class HelperPluginConfiguration : BasePluginConfiguration
{
    public string UpdateHelperUrl { get; set; } = "http://127.0.0.1:8099";
    public bool EnableAutomaticMediaFeatures { get; set; }
    public List<string> AutomaticFeatureChecks { get; set; } = new();
    public bool EnableNativeChapters { get; set; }
    public string ChapterReaderPath { get; set; } = string.Empty;
    public List<ChapterReceipt> ChapterReceipts { get; set; } = new();
}

public sealed class ChapterReceipt
{
    public long OwnerId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string SourceStamp { get; set; } = string.Empty;
    public string Playlist { get; set; } = string.Empty;
}
