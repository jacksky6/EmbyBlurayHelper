using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyBlurayHelper.EmbyPlugin;

[Route("/BlurayHelper/LibraryScope", "GET,POST")]
[Authenticated(Roles = "Admin")]
public sealed class LibraryScopeRequest
{
    public string ServerId { get; set; } = string.Empty;
    public string[] ExcludedLibraryIds { get; set; } = Array.Empty<string>();
}

public sealed partial class BlurayHelperService
{
    public object Get(LibraryScopeRequest request) => new { serverId = Plugin.Instance.ServerId,
        excludedLibraryIds = Plugin.Instance.Configuration.ProcessingScopeServerId == Plugin.Instance.ServerId
            ? Plugin.Instance.Configuration.ExcludedLibraryIds : Array.Empty<string>() };
    public async Task<object> Post(LibraryScopeRequest request)
    {
        if (request.ServerId != Plugin.Instance.ServerId) throw new ArgumentException("Emby 服务器已变化，请重新读取媒体库。");
        if (request.ExcludedLibraryIds is null || request.ExcludedLibraryIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("媒体库范围无效。");
        await ProbeGate.WaitAsync(Request.CancellationToken).ConfigureAwait(false);
        try
        {
            Plugin.Instance.Configuration.ProcessingScopeServerId = request.ServerId;
            Plugin.Instance.Configuration.ExcludedLibraryIds = request.ExcludedLibraryIds.Distinct(StringComparer.Ordinal).ToArray();
            Plugin.Instance.SaveConfiguration();
            Logger.Info("LibraryScope: {0} libraries excluded from disc enrichment, including automatic scans and thumbnails.", request.ExcludedLibraryIds.Length);
            return Get(request);
        }
        finally { ProbeGate.Release(); }
    }
}
