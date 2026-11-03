using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmbyBlurayHelper.App;

internal sealed class EmbyRequestFailure(string message, System.Net.HttpStatusCode statusCode)
    : HttpRequestException(message, null, statusCode)
{
    public static async Task<EmbyRequestFailure> From(HttpResponseMessage response, HttpMethod method,
        string route, string apiKey, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        var detail = Encoding.UTF8.GetString(buffer, 0, length);
        try
        {
            using var json = JsonDocument.Parse(detail);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("ResponseStatus", out var status)) root = status;
                if (root.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.String)
                    detail = message.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        if (!string.IsNullOrEmpty(apiKey)) detail = detail.Replace(apiKey, "[hidden]", StringComparison.Ordinal);
        detail = Regex.Replace(HelperLog.Redact(detail), @"\s+", " ").Trim();
        var hint = detail.Contains("Protocol not found", StringComparison.OrdinalIgnoreCase)
            ? "原版 ffprobe 缺少所需协议，无法读取原盘视频流。" : "";
        return new($"Emby {method} /emby/{route.Split('?')[0]} 失败（HTTP {(int)response.StatusCode}）。{hint}" +
            (detail.Length > 0 ? "返回：" + detail[..Math.Min(detail.Length, 1600)] : "Emby 未返回错误详情，请检查 Emby 日志。"), response.StatusCode);
    }
}
