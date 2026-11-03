using System.Net.Http.Json;
using System.Text.Json;

namespace EmbyBlurayHelper.App;

internal sealed class NativeChapterService(SettingsStore settings, HttpClient client, HelperLog log)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<JsonElement> Send(HelperSettings config, string route, object? body, CancellationToken token)
    {
        if (MediaFeatureCatalog.Connection(config) != MediaFeatureCatalog.Connection(settings.Current))
            throw new InvalidOperationException("Emby 连接已变更，请重新检查媒体库。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(110));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, config.EmbyUrl.TrimEnd('/') + "/emby/BlurayHelper/Chapters" + route);
        request.Headers.Add("X-Emby-Token", config.EmbyApiKey);
        if (body is not null) request.Content = EmbyJsonContent.Create(body);
        using var response = await client.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("原盘章节需要 EmbyBlurayHelper.Plugin 0.1.5 或更新版本。请复制新版 DLL 到 Emby plugins 目录并重启 Emby。");
        if (!response.IsSuccessStatusCode) throw await EmbyRequestFailure.From(response, request.Method, "BlurayHelper/Chapters" + route, config.EmbyApiKey, timeout.Token);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
    }
    internal async Task<object> Status(CancellationToken token)
    {
        try
        {
            var plugin = await Send(settings.Current, "", null, token);
            return new { enabled = settings.Current.EnableNativeChapters, automatic = plugin.GetProperty("enabled").GetBoolean(),
                ready = true, readerPath = MediaFeatureCatalog.Text(plugin, "readerPath"), suggestedReaderPath = ProbeSettings.ExecutablePath,
                checkedItems = plugin.GetProperty("checkedItems").GetInt32(), thumbnails = plugin.TryGetProperty("thumbnails", out var thumbnails) ? thumbnails : (JsonElement?)null,
                error = (string?)null };
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or OperationCanceledException)
        { return new { enabled = settings.Current.EnableNativeChapters, automatic = false, ready = false, readerPath = "", suggestedReaderPath = ProbeSettings.ExecutablePath,
            checkedItems = 0, error = HelperLog.Redact(e.Message) }; }
    }
    internal async Task<object> Configure(bool enabled, string? readerPath, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var path = string.IsNullOrWhiteSpace(readerPath) ? ProbeSettings.ExecutablePath : readerPath.Trim();
            var response = await Send(settings.Current, "", new { Enabled = enabled, ReaderPath = path }, token);
            if (response.GetProperty("enabled").GetBoolean() != enabled || MediaFeatureCatalog.Text(response, "readerPath") != path)
                throw new InvalidOperationException("Emby 未确认章节配置，请刷新后重试。");
            settings.Save(settings.Current with { EnableNativeChapters = enabled });
            log.Add("Information", "NativeChapters", enabled ? "原盘章节已开启：新入库 BDMV / Blu-ray ISO 自动读取 MPLS；已有影片可按媒体库补全。关闭功能不删除已保存章节。" : "原盘章节已关闭，已保存章节保留。");
            return await Status(token);
        }
        finally { gate.Release(); }
    }
}
