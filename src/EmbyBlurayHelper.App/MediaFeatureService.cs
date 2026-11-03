using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.ProbeWrapper;

namespace EmbyBlurayHelper.App;

internal sealed class MediaFeatureService(SettingsStore settings, EmbyIntegration integration, HttpClient client, HelperLog log)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null, HelperSettings? config = null)
    {
        config ??= settings.Current;
        var request = new HttpRequestMessage(method, config.EmbyUrl.TrimEnd('/') + "/emby/BlurayHelper/" + path);
        request.Headers.Add("X-Emby-Token", config.EmbyApiKey);
        if (body is not null) request.Content = EmbyJsonContent.Create(body);
        return request;
    }
    private async Task<JsonObject> Send(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = Request(method, path, body);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("请安装 EmbyBlurayHelper.Plugin 0.1.3 或更新版本，并重启 Emby 后重试。");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token) ?? throw new InvalidDataException("插件返回为空。");
    }
    public async Task<object> Status(CancellationToken token)
    {
        bool? automatic = null;
        var count = 0;
        bool? originalTrackTitles = null;
        var probeConnected = false;
        string? error = null;
        try
        {
            var plugin = await Send(HttpMethod.Get, "MediaFeatures", null, token);
            automatic = plugin["enabled"]?.GetValue<bool>();
            count = plugin["checkedItems"]?.GetValue<int>() ?? 0;
            originalTrackTitles = plugin["originalTrackTitles"]?.GetValue<bool>();
            var probe = await integration.GetProbeStatus(token);
            probeConnected = probe.State == "connected";
            if (settings.Current.EnableMediaFeatures && !probeConnected)
                error = "Emby 尚未接入本工具的探测器，请重新保存设置以接入。";
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or OperationCanceledException)
        { error = HelperLog.Redact(e.Message); }
        return new { enabled = settings.Current.EnableMediaFeatures, probePath = settings.Current.FeatureProbePath,
            probeAccessible = File.Exists(settings.Current.FeatureProbePath), probeConnected, automatic, originalTrackTitles, checkedItems = count, error };
    }
    public async Task<object> Configure(bool enabled, string probePath, bool? originalTrackTitles, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var config = settings.Current with { EnableMediaFeatures = enabled, FeatureProbePath = probePath.Trim() };
            SettingsStore.Validate(config);
            if (enabled)
            {
                await CheckEngine(config.FeatureProbePath, token);
                if ((await integration.GetProbeStatus(token)).State != "connected")
                    await integration.ApplyProbe(false, token);
            }
            // The plugin switch is acknowledged before persisting the helper switch.
            var verified = await Send(HttpMethod.Post, "MediaFeatures", new { Enabled = enabled, ShowOriginalTrackTitles = originalTrackTitles }, token);
            if (verified["enabled"]?.GetValue<bool>() != enabled)
                throw new InvalidOperationException("Emby 自动识别开关未确认保存，请重试。");
            if (originalTrackTitles.HasValue && verified["originalTrackTitles"]?.GetValue<bool>() != originalTrackTitles.Value)
                throw new InvalidOperationException("Emby 轨道标题显示设置未确认保存，请刷新后重试。");
            settings.Save(settings.Current with { EnableMediaFeatures = enabled, FeatureProbePath = config.FeatureProbePath });
            log.Add("Information", "MediaFeatures", enabled
                ? "音视频规格识别已启用。新入库原盘自动处理；原版 ffprobe 继续负责基础探测，独立增强 ffprobe 仅补充已确认的 Atmos / Dolby Vision。"
                : "音视频规格识别已关闭，已保存的影片信息保留。");
            return await Status(token);
        }
        finally { gate.Release(); }
    }
    public static async Task<string> CheckEngine(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || ProbeSettings.IsOurPath(path))
            throw new ArgumentException("请填写可执行的独立增强 ffprobe 完整路径。");
        var start = ProbeProcess.CreateStartInfo(path);
        start.ArgumentList.Add("-version");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("增强 ffprobe 无法启动。");
        using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
        if (process.ExitCode != 0 || !(await output).Contains("ffprobe version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("增强 ffprobe 运行检查失败。");
        return (await output).Split('\n')[0].Trim();
    }
    public async Task<object> Probe(string itemId, CancellationToken token)
    {
        if (!settings.Current.EnableMediaFeatures) throw new InvalidOperationException("请先开启音视频规格识别。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(itemId, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("请输入影片 ID。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        log.Add("Information", "MediaFeatures", $"开始核验影片 {itemId} 的音视频规格；仅补充扩展信息。");
        var result = await Send(HttpMethod.Post, "MediaFeatures/Probe", new { ItemId = itemId }, timeout.Token);
        var descriptions = result["streams"]?.AsArray().OfType<JsonObject>().Select(s =>
            $"#{s["Index"] ?? s["index"]} {s["Codec"] ?? s["codec"]}: {s["Title"] ?? s["title"] ?? s["Profile"] ?? s["profile"] ?? s["ExtendedVideoType"] ?? s["extendedVideoType"]}");
        log.Add("Information", "MediaFeatures", $"影片 {result["name"]}（{itemId}）规格核验完成：{string.Join("；", descriptions ?? [])}");
        return result;
    }

    internal async Task<System.Text.Json.JsonElement> ProbeSource(HelperSettings config, string itemId, string sourceId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = Request(HttpMethod.Post, "MediaFeatures/Probe", new { ItemId = itemId, MediaSourceId = sourceId }, config);
        using var response = await client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw await EmbyRequestFailure.From(response, HttpMethod.Post, "BlurayHelper/MediaFeatures/Probe", config.EmbyApiKey, timeout.Token);
        return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: timeout.Token);
    }
}
