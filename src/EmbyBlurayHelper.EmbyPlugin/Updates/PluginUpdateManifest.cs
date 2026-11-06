using System.Runtime.Serialization;

namespace EmbyBlurayHelper.EmbyPlugin.Updates;

[DataContract]
public sealed class PluginUpdateManifest
{
    public const string AssemblyIdentity = "EmbyBlurayHelper.Plugin";
    public const string FileName = AssemblyIdentity + ".dll";
    public const string ManifestRoute = "plugin-update/manifest.json";
    public const string DownloadRoute = "plugin-update/" + FileName;
    public const int MaximumBytes = 8 * 1024 * 1024;

    [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [DataMember(Name = "assemblyName")] public string AssemblyName { get; set; } = AssemblyIdentity;
    [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
    [DataMember(Name = "size")] public long Size { get; set; }
    [DataMember(Name = "sha256")] public string Sha256 { get; set; } = string.Empty;
}
