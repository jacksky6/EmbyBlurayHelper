using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyBlurayHelper.EmbyPlugin.Updates;

[Route("/BlurayHelper/PluginUpdate", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class PluginUpdateStatusRequest { }
[Route("/BlurayHelper/PluginUpdate/Source", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class PluginUpdateSourceRequest { public string HelperUrl { get; set; } = string.Empty; }
[Route("/BlurayHelper/PluginUpdate/Check", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class PluginUpdateCheckRequest { }
[Route("/BlurayHelper/PluginUpdate/Install", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class PluginUpdateInstallRequest
{
    public string Version { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class PluginUpdateService : BaseApiService
{
    [AutoInjectProperty] public IHttpClient HttpClient { get; set; } = null!;
    private static Plugin Plugin => Plugin.Instance;
    private Task<Stream> Fetch(Uri uri, CancellationToken token) => HttpClient.Get(new MediaBrowser.Common.Net.HttpRequestOptions
    { Url = uri.AbsoluteUri, CancellationToken = token, TimeoutMs = 30000, CacheMode = CacheMode.Unconditional,
        UserAgent = "EmbyBlurayHelper/" + typeof(Plugin).Assembly.GetName().Version, EnableDefaultUserAgent = false });
    private static object View(PluginUpdateStatus s) => new { loadedVersion = s.LoadedVersion, installedVersion = s.InstalledVersion,
        availableVersion = s.AvailableVersion, sha256 = s.Sha256, sourceUrl = s.SourceUrl, state = s.State, message = s.Message,
        checkedAt = s.CheckedAt, restartRequired = s.RestartRequired, canInstall = s.CanInstall };
    public object Get(PluginUpdateStatusRequest request) => View(Plugin.Updater.Status(Plugin.Configuration.UpdateHelperUrl));
    public object Post(PluginUpdateSourceRequest request)
    {
        Plugin.Configuration.UpdateHelperUrl = PluginUpdater.Source(request.HelperUrl).TrimEnd('/');
        Plugin.SaveConfiguration();
        Logger.Info("PluginUpdate: saved Helper update source {0}.", Plugin.Configuration.UpdateHelperUrl);
        return Get(new PluginUpdateStatusRequest());
    }
    public async Task<object> Post(PluginUpdateCheckRequest request)
    {
        try
        {
            var status = await Plugin.Updater.Check(Plugin.Configuration.UpdateHelperUrl, Fetch, Request.CancellationToken).ConfigureAwait(false);
            Logger.Info("PluginUpdate: loaded {0}; installed {1}; available {2}; {3}.", status.LoadedVersion, status.InstalledVersion, status.AvailableVersion, status.State);
            return View(status);
        }
        catch (Exception e)
        {
            Logger.Error("PluginUpdate: check failed: {0}", e.Message);
            throw;
        }
    }
    public async Task<object> Post(PluginUpdateInstallRequest request)
    {
        try
        {
            var status = await Plugin.Updater.Install(Plugin.Configuration.UpdateHelperUrl, request.Version, request.Sha256, Fetch,
                ApplicationHost.NotifyPendingRestart, Request.CancellationToken).ConfigureAwait(false);
            Logger.Info("PluginUpdate: installed {0}; previous DLL backed up; Emby restart required.", status.InstalledVersion);
            return View(status);
        }
        catch (Exception e)
        {
            Logger.Error("PluginUpdate: update failed: {0}", e.Message);
            throw;
        }
    }
}
