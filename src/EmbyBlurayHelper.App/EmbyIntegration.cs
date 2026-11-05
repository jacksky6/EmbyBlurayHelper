using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.ProbeWrapper;
using EmbyBlurayHelper.Proxy;

namespace EmbyBlurayHelper.App;

internal sealed class EmbyIntegration(SettingsStore settings, HttpClient client, HelperLog log)
{
    private readonly SemaphoreSlim probeChange = new(1, 1);
    public object GetMultiClipStatus()
    {
        var enabled = settings.Current.EnableInfuseMultiClipPlayback;
        return new { enabled, readerAvailable = true, reader = "built-in", message = !enabled ? "当前按原方式转发，未启用多片段兼容。"
            : "内置读取器已就绪：通过 MPLS / CLPI 验证完整片段后按需读取，无需 FFmpeg 蓝光协议。具体影片是否支持及触发可查看运行日志。" };
    }

    public async Task<object> SetMultiClipEnabled(bool enabled, CancellationToken token)
    {
        await probeChange.WaitAsync(token);
        try
        {
            settings.Save(settings.Current with { EnableInfuseMultiClipPlayback = enabled });
            log.Add("Information", "MultiClip", enabled ? "Infuse 多片段原盘兼容已开启。" : "Infuse 多片段原盘兼容已关闭。");
            return GetMultiClipStatus();
        }
        finally { probeChange.Release(); }
    }
    private string BackupPath => Path.Combine(settings.DataDirectory, "emby-probe-backup.json");
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, settings.Current.EmbyUrl.TrimEnd('/') + "/emby/" + path);
        if (settings.Current.EmbyApiKey.Length > 0) request.Headers.Add("X-Emby-Token", settings.Current.EmbyApiKey);
        return request;
    }

    private async Task<JsonObject> Options(CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, "Encoding/FfmpegOptions");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var envelope = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token))!;
        return envelope["Object"] is JsonObject options ? (JsonObject)options.DeepClone() : envelope;
    }

    private static string? OriginalProbe(JsonObject? options)
    {
        // ProbePath / CustomProbePath may already point at this helper or another wrapper.
        var path = options?["OriginalProbePath"]?.GetValue<string>()?.Trim();
        return string.IsNullOrEmpty(path) || ProbeSettings.IsOurPath(path) ? null : path;
    }

    private static bool ProbeAccessible(string? path) => path is not null && Path.IsPathFullyQualified(path) && File.Exists(path);

    public async Task<object> DiscoverProbe(string embyUrl, string apiKey, CancellationToken token)
    {
        SettingsStore.Validate(settings.Current with { EmbyUrl = embyUrl, EmbyApiKey = apiKey });
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请先填写 Emby API key，再获取原版探测器路径。");
        using var request = new HttpRequestMessage(HttpMethod.Get, embyUrl.TrimEnd('/') + "/emby/Encoding/FfmpegOptions");
        request.Headers.Add("X-Emby-Token", apiKey);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
        var path = OriginalProbe(envelope?["Object"] as JsonObject ?? envelope);
        var accessible = ProbeAccessible(path);
        return new { path, accessible, message = path is null
            ? "此 Emby 版本未返回原版 ffprobe 路径，请检测本机或参考下方路径手动填写。"
            : accessible ? "已从 Emby 获取原版 ffprobe 路径；保存后可点击「检查 ffprobe」验证运行。"
            : "Emby 返回了原版路径，但本工具无法访问该文件。请确认是否位于另一台主机或另一个容器，并填写本工具内可执行的路径。" };
    }

    public async Task<object> Test(CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, "System/Info/Public");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
        JsonObject? options = null;
        if (settings.Current.EmbyApiKey.Length > 0) options = await Options(token);
        log.Add("Information", "Emby", "Emby 连接测试成功。");
        return new { name = info?["ServerName"]?.ToString(), version = info?["Version"]?.ToString(),
            probeEnabled = options?["UseCustomProbePath"]?.GetValue<bool>(), probePath = options?["CustomProbePath"]?.ToString(),
            apiAuthenticated = options is not null, backupAvailable = File.Exists(BackupPath),
            originalProbePath = OriginalProbe(options), originalProbeAccessible = ProbeAccessible(OriginalProbe(options)),
            integration = options is null ? UnknownStatus("请在「连接与账号」中保存 API key，才能读取实际接入状态。") : DescribeProbe(options) };
    }

    public async Task<ProbeStatus> GetProbeStatus(CancellationToken token)
    {
        if (settings.Current.EmbyApiKey.Length == 0)
            return UnknownStatus("请在「连接与账号」中保存 API key，才能读取实际接入状态。");
        try { return DescribeProbe(await Options(token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException e)
        {
            return UnknownStatus(e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "Emby 拒绝读取配置，请检查 API key 及其管理权限。"
                : "暂时无法读取 Emby 配置，请检查后台地址及 Emby 是否正在运行。");
        }
        catch (Exception e) when (e is OperationCanceledException or JsonException or InvalidOperationException or IOException)
        { return UnknownStatus("读取 Emby 配置未完成，请检查连接后刷新状态。"); }
    }

    private ProbeStatus UnknownStatus(string message) => new("unknown", settings.Current.EnableProbeLanguagePatch,
        null, null, ProbeSettings.ExecutablePath, false, false, message);

    private ProbeStatus DescribeProbe(JsonObject options)
    {
        if (options["UseCustomProbePath"] is not JsonValue enabledValue || !enabledValue.TryGetValue<bool>(out var enabled))
            return UnknownStatus("Emby 未返回有效的探测器配置，暂时无法确认接入状态。");
        var path = options["CustomProbePath"]?.ToString();
        var owned = ProbeSettings.IsOurPath(path);
        var state = !enabled ? "disconnected" : owned ? "connected" : "other";
        var backupValid = false;
        string? message = null;
        if (File.Exists(BackupPath))
        {
            try
            {
                var backup = JsonNode.Parse(File.ReadAllText(BackupPath));
                backupValid = IsUsableBackup(backup);
                if (!backupValid) message = "探测器备份不可用或与当前 Emby 不匹配，请检查数据目录中的备份。";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { message = "无法读取原探测器备份，请检查数据目录中的备份。"; }
        }
        else if (state == "connected") message = "没有原探测器备份；如需解除接入，请在 Emby 中手动恢复探测器设置。";
        return new(state, settings.Current.EnableProbeLanguagePatch, enabled, path, ProbeSettings.ExecutablePath,
            state != "connected" && message is null, owned && backupValid, message);
    }

    internal sealed record ProbeStatus(string State, bool LanguagePatchEnabled, bool? CustomProbeEnabled,
        string? CurrentProbePath, string ExpectedProbePath, bool CanApply, bool CanRestore, string? Message);

    private bool IsUsableBackup(JsonNode? backup) => backup is JsonObject value &&
        value["EmbyUrl"]?.ToString()?.TrimEnd('/') == settings.Current.EmbyUrl.TrimEnd('/') &&
        value["UseCustomProbePath"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) &&
        value.ContainsKey("CustomProbePath") && !(enabled && ProbeSettings.IsOurPath(value["CustomProbePath"]?.ToString()));

    public async Task SaveSettings(HelperSettings input, CancellationToken token)
    {
        await probeChange.WaitAsync(token);
        try { settings.Save(input with { EnableMediaFeatures = settings.Current.EnableMediaFeatures, FeatureProbePath = settings.Current.FeatureProbePath,
            EnableNativeChapters = settings.Current.EnableNativeChapters }); }
        finally { probeChange.Release(); }
    }

    public async Task<object> SetProbeEnabled(bool enabled, CancellationToken token)
    {
        if (!enabled && settings.Current.EnableMediaFeatures)
            throw new InvalidOperationException("音视频规格识别仍在使用探测器接入，请先关闭该功能，再恢复原探测器。");
        await probeChange.WaitAsync(token);
        try
        {
            if (settings.Current.EmbyApiKey.Length == 0) throw new ArgumentException("请先在「连接与账号」中保存 API key。");
            var options = await Options(token);
            var status = DescribeProbe(options);
            if (status.State == "unknown") throw new InvalidOperationException(status.Message);
            if (enabled)
            {
                if (status.State != "connected") await ChangeProbe(false, options, token);
                else if (!settings.Current.EnableProbeLanguagePatch) await CheckProbe(token);
            }
            else if (ProbeSettings.IsOurPath(options["CustomProbePath"]?.ToString()))
                await ChangeProbe(true, options, token);
            // Commit the language switch only after Emby has accepted and verified the change.
            // Disabling while another probe is selected must leave that probe untouched.
            settings.Save(settings.Current with { EnableProbeLanguagePatch = enabled });
            var verified = DescribeProbe(await Options(token));
            log.Add("Information", "Probe", enabled ? "语言补全已开启，Emby 已接入本工具。" : "语言补全已关闭，Emby 已使用原探测器配置。");
            return new { enabled, status = verified, message = enabled ? "已开启语言补全并接入 Emby。" : "已关闭语言补全，已恢复原探测器配置。" };
        }
        finally { probeChange.Release(); }
    }

    public async Task<object> ApplyProbe(bool restore, CancellationToken token)
    {
        await probeChange.WaitAsync(token);
        try { return await ChangeProbe(restore, await Options(token), token); }
        finally { probeChange.Release(); }
    }

    private async Task<object> ChangeProbe(bool restore, JsonObject options, CancellationToken token)
    {
            if (restore)
            {
                if (!File.Exists(BackupPath)) throw new ArgumentException("没有可恢复的探测器配置备份。");
                var backup = JsonNode.Parse(await File.ReadAllTextAsync(BackupPath, token))!;
                if (backup["EmbyUrl"]?.ToString()?.TrimEnd('/') != settings.Current.EmbyUrl.TrimEnd('/'))
                    throw new ArgumentException("备份属于另一个 Emby 地址，请先切回接入时的后台地址。");
                if (!IsUsableBackup(backup)) throw new ArgumentException("原探测器备份无效，无法恢复，请检查数据目录中的备份。");
                if (!ProbeSettings.IsOurPath(options["CustomProbePath"]?.ToString()))
                    throw new ArgumentException("Emby 当前探测器已被其他配置修改，请手动确认后恢复。");
                options["UseCustomProbePath"] = backup["UseCustomProbePath"]?.DeepClone();
                options["CustomProbePath"] = backup["CustomProbePath"]?.DeepClone();
            }
            else
            {
                await CheckProbe(token);
                var executable = ProbeSettings.ExecutablePath;
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请先使用发布后的 EmbyBlurayHelper 可执行文件运行，再配置 Emby。");
                if (File.Exists(BackupPath) && JsonNode.Parse(await File.ReadAllTextAsync(BackupPath, token))?["EmbyUrl"]?.ToString()?.TrimEnd('/') != settings.Current.EmbyUrl.TrimEnd('/'))
                    throw new ArgumentException("现有探测器备份属于另一个 Emby 地址。请先备份数据目录并处理旧备份，再接入新服务器。");
                if (!ProbeSettings.IsOurPath(options["CustomProbePath"]?.ToString()))
                {
                    var backup = new JsonObject { ["EmbyUrl"] = settings.Current.EmbyUrl,
                        ["UseCustomProbePath"] = options["UseCustomProbePath"]?.DeepClone(), ["CustomProbePath"] = options["CustomProbePath"]?.DeepClone() };
                    var temporary = BackupPath + ".new";
                    await File.WriteAllTextAsync(temporary, backup.ToJsonString(), token);
                    SettingsStore.Restrict(temporary);
                    File.Move(temporary, BackupPath, true);
                }
                else if (!File.Exists(BackupPath))
                    throw new InvalidOperationException("当前探测器已指向本工具，但原配置备份缺失，请先在 Emby 中恢复原探测器设置。");
                options["UseCustomProbePath"] = true;
                options["CustomProbePath"] = executable;
            }
            using var request = Request(HttpMethod.Post, "Encoding/FfmpegOptions");
            request.Content = JsonContent.Create(options);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var verified = await Options(token);
            if (verified["UseCustomProbePath"]?.ToJsonString() != options["UseCustomProbePath"]?.ToJsonString() ||
                verified["CustomProbePath"]?.ToString() != options["CustomProbePath"]?.ToString())
                throw new InvalidOperationException("Emby 返回的配置与提交内容不一致，请检查 Emby 日志。");
            log.Add("Information", "Probe", restore ? "已恢复 Emby 原探测器设置。" : "Emby 已使用本工具作为自定义探测器；原设置已备份。");
            return new { message = restore ? "已恢复原探测器设置。" : "已接入 Emby。下一次实际媒体探测时生效。" };
    }

    public async Task<object> CheckProbe(CancellationToken token)
    {
        var path = settings.Current.UpstreamProbePath;
        if (path.Length == 0 || !File.Exists(path)) throw new ArgumentException("请先保存 Emby 原版 ffprobe 的有效完整路径。");
        if (Path.GetFullPath(path) == Environment.ProcessPath || Path.GetFullPath(path) == ProbeSettings.ExecutablePath) throw new ArgumentException("原版 ffprobe 不能指向本工具。");
        using var process = new Process { StartInfo = ProbeProcess.CreateStartInfo(path) };
        process.StartInfo.ArgumentList.Add("-version");
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception exception)
        {
            var message = $"无法启动 ffprobe（系统错误 {exception.NativeErrorCode}）：{exception.Message}";
            log.Add("Error", "Probe", message);
            throw new InvalidOperationException(message, exception);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var text = (await output) + (await error);
        if (process.ExitCode != 0)
        {
            var detail = HelperLog.Redact(text.Trim());
            var message = $"ffprobe 检查失败，退出码：{process.ExitCode}。{detail[..Math.Min(detail.Length, 2048)]}";
            log.Add("Error", "Probe", message);
            throw new InvalidOperationException(message);
        }
        var version = text.Split('\n').FirstOrDefault() ?? "ffprobe";
        log.Add("Information", "Probe", "原版探测器检查成功：" + version);
        return new { message = version };
    }

    public async Task PrepareUninstall(CancellationToken token)
    {
        if (!File.Exists(BackupPath)) return;
        var options = await Options(token);
        if (ProbeSettings.IsOurPath(options["CustomProbePath"]?.ToString()))
            await ApplyProbe(true, token);
    }

    public async Task<object> Inspect(string itemId, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(itemId, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("请输入影片 ID，例如 12。");
        using var request = Request(HttpMethod.Get, "Items?Ids=" + Uri.EscapeDataString(itemId) + "&Fields=MediaSources,MediaStreams");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var items = document.RootElement.GetProperty("Items");
        if (items.GetArrayLength() == 0) throw new ArgumentException("未找到影片，请检查影片 ID 与 API key 权限。");
        var item = items[0];
        var sources = new List<object>();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        foreach (var source in item.GetProperty("MediaSources").EnumerateArray())
        {
            var path = source.GetProperty("Path").GetString();
            var types = new Dictionary<ushort, string>();
            var map = BlurayLanguageMap.Load(source, factory.CreateLogger("Disc"), types);
            sources.Add(new { path, readable = Directory.Exists(path),
                languages = map?.OrderBy(p => p.Key).Select(p => new { pid = "0x" + p.Key.ToString("X4"), language = p.Value,
                    type = types.GetValueOrDefault(p.Key, "Unknown") }).ToArray() });
        }
        return new { name = item.GetProperty("Name").GetString(), sources };
    }

    public static object Detect()
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Emby-Server", "system", "ffprobe.exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "system", "ffprobe.exe"));
            foreach (var process in Process.GetProcessesByName("EmbyServer"))
            {
                using (process)
                {
                    try { if (process.MainModule?.FileName is string exe) paths.Add(Path.Combine(Path.GetDirectoryName(exe)!, "ffprobe.exe")); }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var root in new[] { "/Applications/EmbyServer.app/Contents/MacOS", "/Applications/EmbyServer.app/Contents/Resources", "/Applications/EmbyServer" })
                paths.Add(Path.Combine(root, "ffprobe"));
        }
        else
        {
            foreach (var path in new[] { "/var/packages/EmbyServer/target/bin/ffprobe", "/var/packages/EmbyServer/target/ffmpeg/bin/ffprobe", "/var/packages/EmbyServer/target/ffprobe", "/opt/emby-server/bin/ffprobe", "/usr/lib/emby-server/bin/ffprobe", "/app/emby/bin/ffprobe" }) paths.Add(path);
            if (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv")) paths.Add("/bin/ffprobe");
        }
        return new { probePaths = paths.Where(File.Exists).ToArray(), suggestedEmbyUrl = "http://127.0.0.1:8096", platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux / DSM / Docker" };
    }
}
