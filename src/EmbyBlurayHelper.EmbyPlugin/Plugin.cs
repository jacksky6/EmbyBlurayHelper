using System.Runtime.InteropServices;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

[assembly: Guid("cbe8e7e0-9155-48e9-8f45-912b7062c447")]

namespace EmbyBlurayHelper.EmbyPlugin;

public sealed class Plugin : BasePlugin<HelperPluginConfiguration>
{
    public static Plugin Instance { get; private set; } = null!;
    internal static readonly SemaphoreSlim ProbeGate = new(1, 1);
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) { Instance = this; }
    public override string Name => "EmbyBlurayHelper";

    public override string Description => "Provides explicit Blu-ray media probing for ISO and BDMV sources.";
}

public sealed class HelperPluginConfiguration : BasePluginConfiguration
{
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
