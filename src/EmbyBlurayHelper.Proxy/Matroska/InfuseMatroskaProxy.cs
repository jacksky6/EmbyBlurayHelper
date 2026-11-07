using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EmbyBlurayHelper.Proxy.DolbyVision;

namespace EmbyBlurayHelper.Proxy.Matroska;

// Shared HTTP presentation for copy-only MKV and P7 conversion. Disc planning,
// bounded fragment generation and cache remain in the shared streaming engine.
internal sealed class InfuseMatroskaProxy(ProxyConfiguration config, HttpClient client, DvRuntime runtime, ILogger logger)
{
    private static readonly Regex MetadataRoute = new("^(?:/emby)?/(?:Items/[^/]+/PlaybackInfo|(?:Users/[^/]+/)?Items(?:/[^/]+)?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private bool Enabled => config.EnableInfuseDolbyVision && runtime.Available || config.InfuseDiscContainer == "mkv" && runtime.RemuxAvailable;
    private bool? Mode(JsonElement source) => config.EnableInfuseDolbyVision && runtime.Available && DvDiscPlan.Candidate(source) ? true
        : config.InfuseDiscContainer == "mkv" && runtime.RemuxAvailable && DvDiscPlan.DiscCandidate(source) ? false : null;
    private static VirtualMediaName Name(string path, bool convert) => convert ? VirtualMediaName.DolbyVision(path) : VirtualMediaName.Remux(path);
    private static string Flag(bool convert) => convert ? "HelperDv" : "HelperRemux";
    internal bool IsMetadata(HttpContext context) => Enabled && EmbyProxy.IsInfuse(context.Request) && MetadataRoute.IsMatch(context.Request.Path);
    private static string Text(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";
    private static string Caller(HttpContext context) => string.Concat(new[] { "api_key", "X-Emby-Token", "X-Emby-Authorization", "UserId" }
        .Where(n => !string.IsNullOrEmpty(context.Request.Query[n])).Select(n => "&" + n + "=" + Uri.EscapeDataString(context.Request.Query[n].ToString())));
    private async Task<JsonElement> Catalog(HttpContext context, string[] ids)
    {
        using var request = EmbyProxy.CreateRequest(context, new Uri(config.UpstreamBaseUrl.TrimEnd('/') + "/emby/Items?Ids=" + Uri.EscapeDataString(string.Join(',', ids)) + "&Fields=MediaSources,Chapters" + Caller(context)), "GET", false);
        request.Headers.Remove("Range"); EmbyProxy.PrepareMediaRequest(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        if (!response.IsSuccessStatusCode) throw new CatalogException((int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: context.RequestAborted); return document.RootElement.Clone();
    }
    private sealed class CatalogException(int status) : IOException { internal int Status = status; }
    internal async Task<byte[]?> RewriteMetadataAsync(HttpContext context, byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) return null;
        JsonNode? root; try { root = JsonNode.Parse(bytes); } catch (JsonException) { return null; }
        var objects = root switch { JsonObject o when o["Items"] is JsonArray a => a.OfType<JsonObject>().ToArray(), JsonObject o => [o], JsonArray a => a.OfType<JsonObject>().ToArray(), _ => [] };
        var routeId = Regex.Match(context.Request.Path, "(?i)/Items/([^/]+)").Groups[1].Value;
        var candidates = objects.Where(o => Text(o["Container"]).Contains("bluray", StringComparison.OrdinalIgnoreCase) || Text(o["Container"]).Equals("iso", StringComparison.OrdinalIgnoreCase) || Text(o["Path"]).EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
            o["MediaSources"] is JsonArray sources && sources.OfType<JsonObject>().Any(s =>
                Text(s["Container"]).Contains("bluray", StringComparison.OrdinalIgnoreCase) || Text(s["Path"]).EndsWith(".iso", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length == 0) return null;
        var ids = candidates.Select(o => Text(o["Id"]) is { Length: > 0 } id ? id : routeId).Where(id => id.Length > 0).Distinct().ToArray();
        if (ids.Length == 0) return null;
        JsonElement catalog;
        try { catalog = await Catalog(context, ids); }
        catch (Exception e) when (e is IOException or HttpRequestException) { logger.LogWarning("Infuse DV: catalog unavailable ({Type}); metadata unchanged.", e.GetType().Name); return null; }
        var known = new Dictionary<string, List<(string Source, string Path, DvRuntime.Presentation Presentation)>>();
        foreach (var item in catalog.GetProperty("Items").EnumerateArray())
        {
            if (!item.TryGetProperty("MediaSources", out var sources)) continue;
            foreach (var source in sources.EnumerateArray().Where(s => Mode(s).HasValue))
            {
                try
                {
                    var presentation = await runtime.Describe(source, context.RequestAborted, Mode(source)!.Value); var id = Text(item, "Id");
                    if (!known.TryGetValue(id, out var list)) known[id] = list = [];
                    list.Add((Text(source, "Id"), Text(source, "Path"), presentation));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
                { logger.LogWarning("Infuse DV: item {Item}; original playback retained: {Reason}", Text(item, "Id"), e.Message); }
            }
        }
        var changed = false;
        foreach (var obj in candidates)
        {
            var id = Text(obj["Id"]) is { Length: > 0 } own ? own : routeId;
            if (!known.TryGetValue(id, out var matches)) continue;
            foreach (var source in (obj["MediaSources"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var match = matches.FirstOrDefault(m => m.Source == Text(source["Id"])); if (match.Presentation is null) continue;
                var plan = match.Presentation.Plan;
                var name = Name(match.Path, plan.ConvertDolbyVision);
                source["Container"] = "mkv"; source["Path"] = name.Path; source["Name"] = name.FileName; source["Size"] = plan.Length;
                source["SupportsDirectPlay"] = true; source["SupportsDirectStream"] = true; source["SupportsTranscoding"] = false;
                source["DirectStreamUrl"] = "/emby/Videos/" + Uri.EscapeDataString(id) + "/stream.mkv?Static=true&MediaSourceId=" + Uri.EscapeDataString(match.Source) + "&" + Flag(plan.ConvertDolbyVision) + "=" + plan.Signature + Caller(context);
                source.Remove("TranscodingUrl"); source.Remove("TranscodingContainer"); source["MimeType"] = "video/x-matroska";
                RewriteStreams(source, plan.ConvertDolbyVision); changed = true;
            }
            if (matches.Count == 1 && (obj["MediaSources"] is not JsonArray all || all.Count == 1))
            {
                if (obj.ContainsKey("Container")) obj["Container"] = "mkv";
                if (obj.ContainsKey("Path")) obj["Path"] = Name(matches[0].Path, matches[0].Presentation.Plan.ConvertDolbyVision).Path;
                if (obj.ContainsKey("Size")) obj["Size"] = matches[0].Presentation.Plan.Length;
                RewriteStreams(obj, matches[0].Presentation.Plan.ConvertDolbyVision); changed = true;
            }
        }
        if (changed) logger.LogInformation("Infuse MKV: metadata {Route}; {Count} eligible disc items; ordinary container {Container}; P7 conversion {DV}; on-demand fragments.", context.Request.Path.Value, known.Count, config.InfuseDiscContainer, config.EnableInfuseDolbyVision);
        return changed ? Encoding.UTF8.GetBytes(root!.ToJsonString()) : null;
    }
    private static void RewriteStreams(JsonObject obj, bool convert)
    {
        if (obj["MediaStreams"] is not JsonArray streams) return;
        var video = streams.OfType<JsonObject>().Where(s => Text(s["Type"]) == "Video").OrderByDescending(s => (int?)s["Width"] ?? 0).FirstOrDefault();
        if (video is null) return;
        var mapping = new Dictionary<int, int>(); var result = new JsonArray(); var internalIndex = 0;
        foreach (var stream in streams.OfType<JsonObject>())
        {
            if (convert && Text(stream["Type"]) == "Video" && stream != video) continue;
            if (Text(stream["Type"]) is not ("Video" or "Audio" or "Subtitle")) continue;
            var copy = (JsonObject)stream.DeepClone(); var external = (bool?)copy["IsExternal"] == true;
            var newIndex = external ? (int?)copy["Index"] ?? result.Count : internalIndex++;
            if ((int?)copy["Index"] is { } index) mapping[index] = newIndex;
            copy["Index"] = newIndex;
            if (convert && stream == video)
            {
                copy["ExtendedVideoType"] = "DolbyVision"; copy["VideoRange"] = "DolbyVision"; copy["ExtendedVideoSubType"] = "DoviProfile81";
                copy["ExtendedVideoSubTypeDescription"] = "Profile 8.1 (HDR10 compatible)";
                copy["Title"] = "4K HEVC · Dolby Vision Profile 8.1 / HDR10"; copy["DisplayTitle"] = copy["Title"]!.DeepClone();
            }
            result.Add(copy);
        }
        obj["MediaStreams"] = result;
        foreach (var field in new[] { "DefaultAudioStreamIndex", "DefaultSubtitleStreamIndex", "VideoStreamIndex" })
            if ((int?)obj[field] is { } old && mapping.TryGetValue(old, out var current)) obj[field] = current;
    }
    internal async Task<bool> TryStreamAsync(HttpContext context, string itemId)
    {
        var explicitDv = context.Request.Query.ContainsKey("HelperDv");
        var explicitRemux = context.Request.Query.ContainsKey("HelperRemux");
        var explicitMkv = explicitDv || explicitRemux;
        if (!EmbyProxy.IsInfuse(context.Request)) { if (explicitMkv) { context.Response.StatusCode = 403; return true; } return false; }
        if (explicitDv && explicitRemux) { context.Response.StatusCode = 409; return true; }
        if (explicitDv && !config.EnableInfuseDolbyVision || explicitRemux && config.InfuseDiscContainer != "mkv")
        { context.Response.StatusCode = 410; return true; }
        if (explicitDv && !runtime.Available || explicitRemux && !runtime.RemuxAvailable)
        { context.Response.StatusCode = 503; return true; }
        if (!Enabled) return false;
        if (!explicitMkv && !string.Equals(context.Request.Query["Static"], "true", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var catalog = await Catalog(context, [itemId]);
            JsonElement source = default; var sourceId = context.Request.Query["MediaSourceId"].ToString();
            foreach (var item in catalog.GetProperty("Items").EnumerateArray().Where(i => Text(i, "Id") == itemId))
                if (item.TryGetProperty("MediaSources", out var sources)) foreach (var s in sources.EnumerateArray())
                    if (Text(s, "Id") == sourceId || sourceId.Length == 0 && sources.GetArrayLength() == 1) { source = s; break; }
            if (source.ValueKind == JsonValueKind.Undefined) { context.Response.StatusCode = 404; return true; }
            var mode = Mode(source);
            if (!mode.HasValue || explicitMkv && (explicitDv != mode.Value))
            { if (explicitMkv) { context.Response.StatusCode = 409; return true; } return false; }
            DvRuntime.Presentation presentation;
            try { presentation = await runtime.Describe(source, context.RequestAborted, mode.Value); }
            catch (Exception e) when (!explicitMkv && e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
            { logger.LogWarning("Infuse DV: item {Item}; unsupported disc keeps original playback: {Reason}", itemId, e.Message); return false; }
            var plan = presentation.Plan;
            if (explicitMkv && context.Request.Query[Flag(mode.Value)].ToString() != plan.Signature) { context.Response.StatusCode = 409; return true; }
            // Visibility alone is insufficient: check Emby's playback policy
            // with the caller's credentials on every HEAD and range/cache hit.
            using var headRequest = EmbyProxy.CreateRequest(context, new Uri(config.UpstreamBaseUrl.TrimEnd('/') + "/emby/Videos/" + Uri.EscapeDataString(itemId) + "/stream?Static=true&MediaSourceId=" + Uri.EscapeDataString(Text(source, "Id")) + Caller(context)), "HEAD", false);
            EmbyProxy.PrepareMediaRequest(headRequest); headRequest.Headers.Remove("Range");
            using var head = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (!head.IsSuccessStatusCode && head.StatusCode is not HttpStatusCode.NotFound and not HttpStatusCode.InternalServerError)
            { context.Response.StatusCode = (int)head.StatusCode; return true; }
            var path = context.Request.Path.Value!.TrimEnd('/');
            if (!path.EndsWith("/stream.mkv", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 307; context.Response.Headers.Location = context.Request.PathBase + path[..(path.LastIndexOf('/') + 1)] + "stream.mkv" +
                    (explicitMkv ? context.Request.QueryString : context.Request.QueryString.Add(Flag(mode.Value), plan.Signature));
                context.Response.Headers.CacheControl = "no-store"; context.Response.ContentLength = 0; return true;
            }
            if (!TryRange(context, plan.Length, plan.Signature, out var start, out var end, out var partial))
            { context.Response.StatusCode = 416; context.Response.Headers.ContentRange = $"bytes */{plan.Length}"; context.Response.ContentLength = 0; return true; }
            var session = Session(context);
            var header = await runtime.Header(presentation, session, context.RequestAborted);
            context.Response.StatusCode = partial ? 206 : 200; context.Response.ContentType = "video/x-matroska"; context.Response.ContentLength = end - start + 1;
            context.Response.Headers.AcceptRanges = "bytes"; context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.ETag = "\"" + plan.Signature + "\"";
            context.Response.Headers.ContentDisposition = Name(Text(source, "Path"), mode.Value).ContentDisposition;
            if (partial) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{plan.Length}";
            if (!HttpMethods.IsHead(context.Request.Method)) await Copy(context, presentation, session, header, start, end);
            logger.LogInformation("Infuse MKV: item {Item}; {Method} bytes {Start}-{End}/{Length}; {Mode}.", itemId, context.Request.Method, start, end, plan.Length, mode.Value ? "P7→P8.1" : "copy-only remux");
            return true;
        }
        catch (CatalogException e) { context.Response.StatusCode = e.Status; return true; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { logger.LogInformation("Infuse DV: item {Item}; request cancelled, unused conversion stopped.", itemId); throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ObjectDisposedException or OperationCanceledException)
        {
            logger.LogWarning("Infuse DV: item {Item}; failed {Type}: {Reason}. No original disc bytes are returned as MKV.", itemId, e.GetType().Name, e.Message);
            if (context.Response.HasStarted) context.Abort(); else { context.Response.Clear(); context.Response.StatusCode = 502; }
            return true;
        }
    }
    private async Task Copy(HttpContext context, DvRuntime.Presentation presentation, string session, byte[] header, long start, long end)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, runtime.Cache.Stopping);
        var token = lifetime.Token;
        var plan = presentation.Plan; var zeros = new byte[65536]; var position = start;
        while (position <= end)
        {
            token.ThrowIfCancellationRequested();
            if (position < header.Length)
            { var count = (int)Math.Min(header.Length - position, end - position + 1); await context.Response.Body.WriteAsync(header.AsMemory((int)position, count), token); position += count; continue; }
            var offset = position - VirtualMatroska.HeaderBytes;
            var segment = FindSegment(plan, offset);
            using var lease = await runtime.Fragment(presentation, session, segment.Number, token);
            var cluster = lease.Fragment.Cluster; var within = offset - segment.SlotOffset;
            var countLeft = Math.Min(segment.SlotBytes - within, end - position + 1);
            while (countLeft > 0)
            {
                ReadOnlyMemory<byte> bytes;
                if (within < cluster.Length) bytes = cluster.AsMemory((int)within, (int)Math.Min(cluster.Length - within, countLeft));
                else if (within < cluster.Length + 9)
                {
                    var pad = new byte[9]; pad[0] = 0xec; Ebml.Size((ulong)(segment.SlotBytes - cluster.Length - 9), 8).CopyTo(pad, 1);
                    bytes = pad.AsMemory((int)(within - cluster.Length), (int)Math.Min(cluster.Length + 9 - within, countLeft));
                }
                else bytes = zeros.AsMemory(0, (int)Math.Min(zeros.Length, countLeft));
                await context.Response.Body.WriteAsync(bytes, token); within += bytes.Length; position += bytes.Length; countLeft -= bytes.Length;
            }
        }
    }
    internal static DvDiscPlan.Segment FindSegment(DvDiscPlan plan, long offset)
    {
        var lo = 0; var hi = plan.Segments.Count - 1;
        while (lo <= hi) { var mid = (lo + hi) / 2; var segment = plan.Segments[mid]; if (offset < segment.SlotOffset) hi = mid - 1; else if (offset >= segment.SlotOffset + segment.SlotBytes) lo = mid + 1; else return segment; }
        throw new IOException("Virtual MKV offset outside its index.");
    }
    internal static bool TryRange(HttpContext context, long length, string signature, out long start, out long end, out bool partial)
    {
        start = 0; end = length - 1; partial = false; var range = context.Request.Headers.Range.ToString();
        if (context.Request.Headers.IfRange.ToString() is { Length: > 0 } validator && validator != "\"" + signature + "\"") range = "";
        if (range.Length == 0) return true;
        if (!RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1) return false;
        var part = parsed.Ranges.Single();
        if (!part.From.HasValue && part.To is not > 0 || part.From >= length || part.From.HasValue && part.To.HasValue && part.To < part.From) return false;
        start = part.From ?? Math.Max(0, length - part.To!.Value); end = part.From.HasValue ? Math.Min(part.To ?? end, end) : end; partial = true; return true;
    }
    private static string Session(HttpContext context) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
        context.Request.Headers["X-Emby-Token"].ToString(), context.Request.Headers["X-Emby-Authorization"].ToString(), context.Request.Headers.Authorization.ToString(),
        context.Request.Query["api_key"].ToString(), context.Request.Query["X-Emby-Token"].ToString(), context.Request.Query["DeviceId"].ToString(),
        context.Request.Query["PlaySessionId"].ToString(), context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString()))));
}
