using System.Text.Json;

namespace EmbyBlurayHelper.Proxy;

internal sealed record ProxyConfiguration
{
    public string ListenUrl { get; init; } = "http://127.0.0.1:8097";
    public string UpstreamBaseUrl { get; init; } = "http://127.0.0.1:8096";
    public bool EnableInfuseLanguagePatch { get; init; } = true;
    public bool EnableInfuseMultiClipPlayback { get; init; }
    public bool EnableInfuseChapters { get; init; }
    public bool EnableInfuseDolbyVision { get; init; }
    public string DolbyVisionFfmpegPath { get; init; } = "";
    public string DolbyVisionLibraryPath { get; init; } = "";
    public int DolbyVisionCacheMiB { get; init; } = 512;
    public int DolbyVisionGlobalCacheMiB { get; init; } = 1024;

    public static ProxyConfiguration Load(string path)
    {
        using var stream = File.OpenRead(path);
        var config = JsonSerializer.Deserialize<ProxyConfiguration>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Proxy configuration is empty.");
        foreach (var url in new[] { config.ListenUrl, config.UpstreamBaseUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidDataException("listenUrl and upstreamBaseUrl must be absolute HTTP(S) URLs.");
        }
        return config;
    }
}
