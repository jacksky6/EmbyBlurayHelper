using System.Net.Http.Json;
using System.Text.Json;

namespace EmbyBlurayHelper.App;

internal sealed record LibraryScopeInput(string ServerId, string[] ExcludedLibraryIds);
internal sealed record ScopeLibrary(string Id, string Name, string CollectionType, string[] Locations, bool Allowed);
internal sealed record LibraryScopeView(string ServerId, ScopeLibrary[] Libraries, bool PluginReady, bool Synchronized, string Message)
{
    public int AllowedCount => Libraries.Count(l => l.Allowed);
    public int ExcludedCount => Libraries.Length - AllowedCount;
}

// One policy for management tasks, Emby's automatic providers, and ffprobe.
// Only IDs are authoritative; paths are a credential-free projection for the probe.
internal sealed class LibraryScopeService(SettingsStore settings, HttpClient client, HelperLog log)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<JsonElement> Send(HelperSettings config, string route, object? body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, config.EmbyUrl.TrimEnd('/') + "/emby/" + route);
        request.Headers.Add("X-Emby-Token", config.EmbyApiKey);
        if (body is not null) request.Content = EmbyJsonContent.Create(body);
        using var response = await client.SendAsync(request, timeout.Token);
        if (route == "BlurayHelper/LibraryScope" && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new NotSupportedException("请在 Emby 插件页面更新到 0.1.9 或更新版本，并重启 Emby，才能同步自动补全的处理范围。");
        if (!response.IsSuccessStatusCode) throw await EmbyRequestFailure.From(response, request.Method, route, config.EmbyApiKey, timeout.Token);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
    }
    private void EnsureConnection(HelperSettings config)
    {
        if (SettingsStore.Connection(settings.Current) != SettingsStore.Connection(config))
            throw new InvalidOperationException("Emby 连接已变化，请重新读取媒体库。");
    }
    private async Task<LibraryScopeView> Load(HelperSettings config, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config.EmbyApiKey)) throw new InvalidOperationException("请先保存 Emby 地址和 API key。");
        var info = await Send(config, "System/Info/Public", null, token);
        var id = MediaFeatureCatalog.Text(info, "Id");
        if (id.Length == 0) throw new InvalidDataException("Emby 未返回服务器 ID，无法保存可靠的处理范围。");
        var raw = await Send(config, "Library/VirtualFolders", null, token);
        if (raw.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Emby 未返回媒体库列表。");
        var libraries = raw.EnumerateArray().Where(l => SettingsStore.LibraryTypeAllowed(MediaFeatureCatalog.Text(l, "CollectionType"))).Select(l => new ScopeLibrary(MediaFeatureCatalog.Text(l, "ItemId"),
            MediaFeatureCatalog.Text(l, "Name"), MediaFeatureCatalog.Text(l, "CollectionType"),
            l.TryGetProperty("Locations", out var locations) && locations.ValueKind == JsonValueKind.Array
                ? locations.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() : [],
            SettingsStore.LibraryAllowed(config, id, MediaFeatureCatalog.Text(l, "ItemId")))).ToArray();
        if (libraries.Any(l => l.Id.Length == 0) || libraries.Select(l => l.Id).Distinct().Count() != libraries.Length)
            throw new InvalidDataException("媒体库 ID 缺失或重复，请检查 Emby。");
        EnsureConnection(config);
        try
        {
            var plugin = await Send(config, "BlurayHelper/LibraryScope", null, token);
            if (MediaFeatureCatalog.Text(plugin, "serverId") != id) throw new InvalidOperationException("插件与 Emby 服务器 ID 不一致。");
            var expected = config.ExcludedLibrariesByServer.GetValueOrDefault(id) ?? [];
            var actual = plugin.GetProperty("excludedLibraryIds").EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
            var roots = libraries.Where(l => !l.Allowed).SelectMany(l => l.Locations).ToHashSet(StringComparer.Ordinal);
            var projectionMatches = roots.SetEquals(SettingsStore.ExcludedPaths(config)) && (expected.Length == 0 || config.ScopeServerId == id);
            return new(id, libraries, true, actual.SetEquals(expected) && projectionMatches, "新增媒体库默认允许处理；系统合集自动过滤。取消勾选并保存后，检查、批量补全和新片自动补全均跳过该库。");
        }
        catch (NotSupportedException e) { return new(id, libraries, false, false, e.Message); }
    }
    internal Task<LibraryScopeView> Read(CancellationToken token) => Load(settings.Current, token);
    internal async Task<LibraryScopeView> Save(LibraryScopeInput input, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var config = settings.Current;
            var view = await Load(config, token);
            if (input.ServerId != view.ServerId) throw new InvalidOperationException("Emby 服务器已变化，请重新读取媒体库。");
            if (!view.PluginReady) throw new InvalidOperationException(view.Message);
            if (input.ExcludedLibraryIds is null || input.ExcludedLibraryIds.Any(id => !view.Libraries.Any(l => l.Id == id)))
                throw new ArgumentException("媒体库已变化，请重新读取后保存。");
            var exclusions = new Dictionary<string, string[]>(config.ExcludedLibrariesByServer, StringComparer.Ordinal)
            { [view.ServerId] = input.ExcludedLibraryIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray() };
            var next = config with { ExcludedLibrariesByServer = exclusions };
            view = view with { Synchronized = false, Libraries = view.Libraries.Select(l => l with { Allowed = SettingsStore.LibraryAllowed(next, view.ServerId, l.Id) }).ToArray() };
            try { await Synchronize(next, view, token); }
            catch
            {
                // An Emby confirmation or local disk failure must not leave a
                // silently different policy in the two processes.
                try { await Send(config, "BlurayHelper/LibraryScope", new { view.ServerId,
                    ExcludedLibraryIds = config.ExcludedLibrariesByServer.GetValueOrDefault(view.ServerId) ?? [] }, CancellationToken.None); }
                catch (Exception e) { log.Add("Error", "LibraryScope", "范围保存失败且无法恢复 Emby 配置，请重新读取并保存范围：" + HelperLog.Redact(e.Message)); }
                throw;
            }
            log.Add("Information", "LibraryScope", $"媒体库处理范围已保存：允许 {view.AllowedCount} 个，排除 {view.ExcludedCount} 个。排除：{string.Join("、", view.Libraries.Where(l => !l.Allowed).Select(l => l.Name))}。既有媒体信息保留。");
            return view with { Synchronized = true };
        }
        finally { gate.Release(); }
    }
    internal async Task<LibraryScopeView> Prepare(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var config = settings.Current;
            var view = await Load(config, token);
            if (!view.PluginReady && view.ExcludedCount > 0) throw new InvalidOperationException(view.Message);
            if (view.PluginReady) await Synchronize(config, view, token);
            return view with { Synchronized = view.PluginReady };
        }
        finally { gate.Release(); }
    }
    private async Task Synchronize(HelperSettings config, LibraryScopeView view, CancellationToken token)
    {
        EnsureConnection(config);
        var excluded = config.ExcludedLibrariesByServer.GetValueOrDefault(view.ServerId) ?? [];
        if (!view.Synchronized)
        {
            var response = await Send(config, "BlurayHelper/LibraryScope", new { view.ServerId, ExcludedLibraryIds = excluded }, token);
            if (MediaFeatureCatalog.Text(response, "serverId") != view.ServerId ||
                !response.GetProperty("excludedLibraryIds").EnumerateArray().Select(v => v.GetString()!).ToHashSet().SetEquals(excluded))
                throw new InvalidOperationException("Emby 未确认媒体库处理范围，未保存 Helper 配置。");
        }
        EnsureConnection(config);
        settings.Save(settings.Current with { ExcludedLibrariesByServer = config.ExcludedLibrariesByServer,
            ScopeConnection = SettingsStore.Connection(config), ScopeServerId = view.ServerId,
            ScopeExcludedPaths = view.Libraries.Where(l => !l.Allowed).SelectMany(l => l.Locations).Distinct(StringComparer.Ordinal).ToArray() });
    }
}
