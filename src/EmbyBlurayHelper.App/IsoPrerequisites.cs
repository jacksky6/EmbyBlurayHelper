using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmbyBlurayHelper.Proxy;

namespace EmbyBlurayHelper.App;

internal sealed record IsoProbeCapability(string State, string Message);
internal sealed record IsoPrerequisiteStatus(string State, bool CanProbe, string PluginState,
    string? PluginVersion, string PluginMessage, string ProbeState, string ProbeMessage, string Message, DateTimeOffset CheckedAt,
    bool BdmvDirectProbe = false, bool PlaylistRepair = false);

// Checks prerequisites only. It never probes a movie, installs a DLL or restarts Emby.
internal sealed class IsoPrerequisites(HttpClient client,
    Func<string, CancellationToken, Task<IsoProbeCapability>>? checkProbe = null)
{
    private sealed record PluginResult(string State, string? Version, string Message,
        bool BdmvDirectProbe = false, bool PlaylistRepair = false);
    public async Task<IsoPrerequisiteStatus> Check(HelperSettings config, CancellationToken token)
    {
        var plugin = await Plugin(config, token);
        var probe = await (checkProbe ?? CheckProbe)(config.UpstreamProbePath, token);
        var ready = plugin.State == "ready" && probe.State == "ready";
        var message = ready ? "ISO 补全前置检查通过；具体影片仍需探测后核验。" +
            (plugin.BdmvDirectProbe ? "已支持异常 BDMV 直接探测。" : "异常 BDMV 修复需更新随附的 0.1.2 或更新版 DLL 并重启 Emby。")
            : string.Join(" ", new[] { plugin.State != "ready" ? plugin.Message : null,
                probe.State != "ready" ? probe.Message : null }.Where(s => s is not null));
        return new(ready ? "ready" : "blocked", ready, plugin.State, plugin.Version, plugin.Message,
            probe.State, probe.Message, message, DateTimeOffset.UtcNow, plugin.BdmvDirectProbe, plugin.PlaylistRepair);
    }

    private async Task<PluginResult> Plugin(HelperSettings config, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config.EmbyApiKey)) return new("unknown", null, "请先保存 Emby 地址和管理员 API key，再检查 ISO 插件。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.EmbyUrl.TrimEnd('/') + "/emby/BlurayHelper/Status");
            request.Headers.Add("X-Emby-Token", config.EmbyApiKey);
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new("not-loaded", null, "Emby 未加载 ISO 插件。请安装 EmbyBlurayHelper.Plugin.dll 并重启 Emby；已复制时请检查文件权限及 Emby 启动日志。");
            if (!response.IsSuccessStatusCode)
                return new("unknown", null, $"无法检查 ISO 插件（HTTP {(int)response.StatusCode}），请检查 Emby 连接、API 权限及日志。");
            var status = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
            if (status.ValueKind != JsonValueKind.Object) throw new JsonException();
            var version = status.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!status.TryGetProperty("status", out var state) || state.GetString() != "ready")
                return new("not-loaded", version, "ISO 插件尚未就绪。若刚安装或更新 DLL，请重启 Emby，再检查文件权限及 Emby 启动日志。");
            if (!status.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
                !capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "missing-runtime"))
                return new("outdated", version, "Emby 已加载的 ISO 插件缺少时长补全能力。请更新至随附的 DLL 并重启 Emby；已替换文件也需重启才会加载新版。");
            var direct = capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "bdmv-direct-probe");
            return new("ready", version, $"Emby 已加载插件 {version ?? "（版本未返回）"}，支持媒体轨道及时长补全。" +
                (direct ? "已支持异常 BDMV 直接探测。" : "异常 BDMV 修复需更新随附的 0.1.2 或更新版 DLL 并重启 Emby。"),
                direct,
                capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "playlist-repair"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("unknown", null, "ISO 插件状态检查超时，请检查 Emby 连接后重试。"); }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        { return new("unknown", null, "无法读取 ISO 插件状态，请检查 Emby 连接及启动日志后重试。"); }
    }

    private static bool HasInputProtocol(string protocols, string protocol)
    {
        var input = false;
        foreach (var line in protocols.Split('\n').Select(s => s.Trim()))
        {
            if (line == "Input:") { input = true; continue; }
            if (line == "Output:") input = false;
            if (input && line == protocol) return true;
        }
        return false;
    }

    internal static IsoProbeCapability ClassifyProtocols(string protocols) =>
        HasInputProtocol(protocols, "http") && HasInputProtocol(protocols, "tcp")
            ? new("ready", "统一使用内置 UDF/M2TS 读取：按需读取 ISO 内媒体，再由 Emby 原版 ffprobe 识别编码；不依赖 bluray 协议，无需挂载、整盘解压或转码。")
        : new("unsupported", "原版 ffprobe 缺少内置 ISO 读取所需的 http/tcp 输入协议；请检查是否选用了 Emby 原版 ffprobe。BDMV 补全仍可使用。");

    private static async Task<IsoProbeCapability> CheckProbe(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new("unknown", "原版 ffprobe 路径无效，尚不能确认蓝光 ISO 读取能力。");
        if (ProbeSettings.IsOurPath(path) || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new("unknown", "请配置 Emby 原版 ffprobe，不能使用辅助探测器或 dotnet 作为原版路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var process = new Process { StartInfo = EmbyToolProcess.CreateStartInfo(path) };
            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-protocols");
            process.Start();
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0 || !stdout.Contains("Input:"))
                return new("unknown", "无法读取原版 ffprobe 的协议列表，请检查路径、执行权限及原版程序依赖。");
            return ClassifyProtocols(stdout);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("unknown", "ffprobe 能力检查超时，请检查原版程序后重试。"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return new("unknown", "无法执行原版 ffprobe 的能力检查，请检查路径、执行权限及依赖。"); }
    }
}
