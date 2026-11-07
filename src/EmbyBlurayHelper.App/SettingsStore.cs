using System.Text.Json;
using EmbyBlurayHelper.Proxy;

namespace EmbyBlurayHelper.App;

internal sealed record HelperSettings
{
    public string ManagementUrl { get; init; } = "http://0.0.0.0:8099";
    public string ProxyUrl { get; init; } = "http://0.0.0.0:8097";
    public string EmbyUrl { get; init; } = "http://127.0.0.1:8096";
    public string UpstreamProbePath { get; init; } = "";
    public string EmbyApiKey { get; init; } = "";
    public bool EnableProbeLanguagePatch { get; init; } = true;
    public bool EnableInfuseLanguagePatch { get; init; } = true;
    public bool EnableInfuseMultiClipPlayback { get; init; }
    public bool EnableInfuseChapters { get; init; }
    public bool EnableInfuseDolbyVision { get; init; }
    public string DolbyVisionFfmpegPath { get; init; } = "";
    public string DolbyVisionLibraryPath { get; init; } = "";
    public int DolbyVisionCacheMiB { get; init; } = 512;
    public int DolbyVisionGlobalCacheMiB { get; init; } = 1024;
    public bool EnableMediaFeatures { get; init; }
    public bool EnableNativeChapters { get; init; }
    public string FeatureProbePath { get; init; } = "";
    // Persist only opt-outs. New libraries are allowed without any migration.
    public Dictionary<string, string[]> ExcludedLibrariesByServer { get; init; } = new(StringComparer.Ordinal);
    public string ScopeConnection { get; init; } = "";
    public string ScopeServerId { get; init; } = "";
    public string[] ScopeExcludedPaths { get; init; } = [];
}

internal sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private HelperSettings current;
    public string DataDirectory { get; }
    public string FilePath => Path.Combine(DataDirectory, "settings.json");
    public HelperSettings Current => Volatile.Read(ref current);
    public HelperSettings Startup { get; }

    internal static string Connection(HelperSettings config) => config.EmbyUrl.TrimEnd('/') + "\n" + config.EmbyApiKey;
    internal static string ConnectionIdentity(HelperSettings config) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Connection(config))));
    internal static string ScopeStamp(HelperSettings config) => string.Join("\n", config.ExcludedLibrariesByServer
        .Where(p => p.Value.Length > 0).OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => p.Key + ":" + string.Join(",", p.Value.OrderBy(id => id, StringComparer.Ordinal))));
    internal static string PlanConnection(HelperSettings config) => Connection(config) +
        (ScopeStamp(config) is { Length: > 0 } scope ? "\nScope:" + scope : "") + "\nLibraryFilter:no-boxsets-v1";
    // Collections are a virtual entry point, not an independently scanned library.
    // Version the catalog rule above so older saved plans must be checked again.
    internal static bool LibraryTypeAllowed(string? type) => !string.Equals(type?.Trim(), "boxsets", StringComparison.OrdinalIgnoreCase);
    internal static bool LibraryAllowed(HelperSettings config, string serverId, string id) =>
        !config.ExcludedLibrariesByServer.TryGetValue(serverId, out var excluded) || !excluded.Contains(id, StringComparer.Ordinal);
    internal static string[] ExcludedPaths(HelperSettings config) => config.ScopeConnection == Connection(config) ? config.ScopeExcludedPaths : [];

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DataDirectory);
        current = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<HelperSettings>(File.ReadAllText(FilePath), Json) ?? throw new InvalidDataException("配置文件为空。")
            : new HelperSettings();
        Validate(current);
        Startup = current;
        if (!File.Exists(FilePath)) Save(current);
        else ProbeSettings.Export(current);
    }

    public void Save(HelperSettings settings)
    {
        Validate(settings);
        lock (gate)
        {
            var temporary = FilePath + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
            Restrict(temporary);
            File.Move(temporary, FilePath, true);
            ProbeSettings.Export(settings);
            Volatile.Write(ref current, settings);
        }
    }

    public ProxyConfiguration ProxyConfiguration() => new()
    {
        ListenUrl = Startup.ProxyUrl,
        UpstreamBaseUrl = Current.EmbyUrl,
        EnableInfuseLanguagePatch = Current.EnableInfuseLanguagePatch,
        EnableInfuseMultiClipPlayback = Current.EnableInfuseMultiClipPlayback,
        EnableInfuseChapters = Current.EnableInfuseChapters,
        EnableInfuseDolbyVision = Current.EnableInfuseDolbyVision,
        DolbyVisionFfmpegPath = DvFfmpeg(Current),
        DolbyVisionLibraryPath = DvLibrary(Current),
        DolbyVisionCacheMiB = Current.DolbyVisionCacheMiB,
        DolbyVisionGlobalCacheMiB = Current.DolbyVisionGlobalCacheMiB
    };

    internal static string DvFfmpeg(HelperSettings settings) => settings.DolbyVisionFfmpegPath.Length > 0 ? settings.DolbyVisionFfmpegPath :
        settings.UpstreamProbePath.Length > 0 ? Path.Combine(Path.GetDirectoryName(settings.UpstreamProbePath)!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg") : "";
    internal static string DvLibrary(HelperSettings settings) => settings.DolbyVisionLibraryPath.Length > 0 ? settings.DolbyVisionLibraryPath :
        Path.Combine(AppContext.BaseDirectory, "tools", OperatingSystem.IsWindows() ? "dovi.dll" : OperatingSystem.IsMacOS() ? "libdovi.dylib" : "libdovi.so");

    public bool RestartRequired => Current.ManagementUrl != Startup.ManagementUrl || Current.ProxyUrl != Startup.ProxyUrl;

    public static void Validate(HelperSettings settings)
    {
        if (settings.EmbyApiKey is null || settings.UpstreamProbePath is null || settings.FeatureProbePath is null)
            throw new ArgumentException("API key 和探测器路径不能为 null；未配置时请留空。");
        if (settings.DolbyVisionFfmpegPath is null || settings.DolbyVisionLibraryPath is null ||
            new[] { settings.DolbyVisionFfmpegPath, settings.DolbyVisionLibraryPath }.Any(p => p.Length > 0 && !Path.IsPathFullyQualified(p)))
            throw new ArgumentException("杜比视界工具路径必须留空或使用完整路径。");
        if (settings.DolbyVisionCacheMiB is < 128 or > 4096 || settings.DolbyVisionGlobalCacheMiB < settings.DolbyVisionCacheMiB || settings.DolbyVisionGlobalCacheMiB > 16384)
            throw new ArgumentException("杜比视界单会话缓存需为 128–4096 MiB，总缓存不得小于单会话缓存且最多 16384 MiB。");
        if (settings.ExcludedLibrariesByServer is null || settings.ScopeExcludedPaths is null ||
            settings.ExcludedLibrariesByServer.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Any(string.IsNullOrWhiteSpace)) ||
            settings.ScopeExcludedPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("媒体库处理范围配置无效。");
        if (settings.FeatureProbePath.Length > 0 && (!Path.IsPathFullyQualified(settings.FeatureProbePath) ||
            string.Equals(settings.FeatureProbePath, settings.UpstreamProbePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new ArgumentException("增强 ffprobe 必须使用独立的完整路径，不能指向 Emby 原版 ffprobe。");
        foreach (var value in new[] { settings.ManagementUrl, settings.ProxyUrl, settings.EmbyUrl })
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("请输入完整的 HTTP 地址，例如 http://127.0.0.1:8096。HTTPS 可由外部反向代理提供。");
        var management = new Uri(settings.ManagementUrl);
        var proxy = new Uri(settings.ProxyUrl);
        if (management.Port == proxy.Port) throw new ArgumentException("管理界面和播放代理需要使用不同端口。");
        var upstream = new Uri(settings.EmbyUrl);
        var local = upstream.IsLoopback || upstream.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.ToString() == upstream.Host);
        if (local && (upstream.Port == proxy.Port || upstream.Port == management.Port))
            throw new ArgumentException("Emby 后台地址不能指向本工具的端口，否则会形成转发循环。");
    }

    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
