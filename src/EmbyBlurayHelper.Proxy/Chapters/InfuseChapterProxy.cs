using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace EmbyBlurayHelper.Proxy.Chapters;

// Separate from TS language patching and Emby database enrichment. Only Infuse
// gets an ISO view, and only when Emby already has a valid chapter list.
internal sealed class InfuseChapterProxy(ProxyConfiguration config, HttpClient client, IMemoryCache cache, ILogger logger,
    Func<JsonElement, DiscChapterPresentation?>? presentationFactory = null)
{
    private static readonly Regex MetadataRoute = new("^(?:/emby)?/(?:Items/[^/]+/PlaybackInfo|(?:Users/[^/]+/)?Items(?:/[^/]+)?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal bool IsMetadata(HttpContext context) => config.EnableInfuseChapters && EmbyProxy.IsInfuse(context.Request) && MetadataRoute.IsMatch(context.Request.Path);

    internal byte[]? RewriteMetadata(HttpContext context, byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); } catch (JsonException) { return null; }
        var changed = false;
        if (root is JsonObject obj)
        {
            if (obj["Items"] is JsonArray items)
            {
                foreach (var item in items.OfType<JsonObject>()) changed |= RewriteItem(context, item);
            }
            else changed = RewriteItem(context, obj);
        }
        else if (root is JsonArray items)
            foreach (var item in items.OfType<JsonObject>()) changed |= RewriteItem(context, item);
        return changed ? Encoding.UTF8.GetBytes(root!.ToJsonString()) : null;
    }

    private bool RewriteItem(HttpContext context, JsonObject obj)
    {
        if (obj["MediaSources"] is not JsonArray sources) return false;
        var itemId = obj["Id"]?.GetValue<string>() ?? Regex.Match(context.Request.Path, "(?i)/Items/([^/]+)").Groups[1].Value;
        if (string.IsNullOrEmpty(itemId)) return false;
        var changed = false;
        foreach (var source in sources.OfType<JsonObject>())
        {
            var presentation = Build(source.Deserialize<JsonElement>());
            var sourceId = source["Id"]?.GetValue<string>();
            if (presentation is null || string.IsNullOrEmpty(sourceId)) continue;
            source["Container"] = "blurayiso"; source["Size"] = presentation.Image.Length;
            var originalPath = source["Path"]?.GetValue<string>() ?? "";
            var separator = originalPath.Contains('\\') ? '\\' : '/';
            source["Path"] = originalPath.TrimEnd('/', '\\') + separator + "EmbyBlurayHelper.iso";
            // Preserve native media-source hints. Infuse can select a different
            // reader when extra URL/MIME hints are injected into the DTO. The
            // authenticated stream route supplies the explicit ISO redirect.
            source["SupportsDirectPlay"] = true; source["SupportsDirectStream"] = true;
            // Infuse can retain item-level information from a library listing.
            // Describe one consistent file at both levels, rather than an ISO
            // media source inside an item still advertised as a BDMV folder.
            if (sources.Count == 1)
            {
                if (obj.ContainsKey("Container")) obj["Container"] = "blurayiso";
                if (obj["Path"]?.GetValue<string>() == originalPath) obj["Path"] = source["Path"]!.DeepClone();
                if (obj.ContainsKey("Size")) obj["Size"] = presentation.Image.Length;
            }
            changed = true;
            logger.LogInformation("Infuse chapters: item {ItemId}; {Count} saved Emby chapters in virtual ISO, playlist {Playlist}, navigation {Bytes} bytes; original media is read on demand; metadata route {Route}.", itemId, presentation.ChapterCount, presentation.Playlist, presentation.Image.MetadataBytes, context.Request.Path.Value);
        }
        return changed;
    }

    private DiscChapterPresentation? Build(JsonElement source)
    {
        var key = "chapter-disc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.GetRawText())));
        if (cache.TryGetValue<DiscChapterPresentation>(key, out var cached) && cached!.IsCurrent) return cached;
        try
        {
            var presentation = presentationFactory is null ? DiscChapterPresentation.Create(source) : presentationFactory(source);
            if (presentation is not null) cache.Set(key, presentation, new MemoryCacheEntryOptions { Size = Math.Max(1, (presentation.Image.MetadataBytes + 1048575) / 1048576), AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
            return presentation;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or JsonException or IndexOutOfRangeException)
        {
            logger.LogWarning("Infuse chapters: presentation unavailable ({ErrorType}); forwarding original playback information.", e.GetType().Name);
            return null;
        }
    }

    internal async Task<bool> TryStreamAsync(HttpContext context, string itemId)
    {
        var signature = context.Request.Query["HelperDisc"].ToString();
        if (signature.Length == 0 && (!config.EnableInfuseChapters || !EmbyProxy.IsInfuse(context.Request) ||
            !string.Equals(context.Request.Query["Static"], "true", StringComparison.OrdinalIgnoreCase))) return false;
        var trace = new StreamTrace();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var range = context.Request.Headers.Range.ToString();
        // Only the numeric byte-range syntax is safe to include in logs. Never
        // log the full URL, query string, authorization headers or user token.
        var safeRange = range.Length == 0 ? "none" : range.Length <= 128 && Regex.IsMatch(range, "^bytes=[0-9, -]+$") ? range : "invalid";
        logger.LogInformation("Infuse chapters: request {RequestId}; item {ItemId}; {Method} {Path}; explicit ISO {Explicit}; range {Range} started.", context.TraceIdentifier, itemId, context.Request.Method, context.Request.Path.Value, signature.Length > 0, safeRange);
        try
        {
            var handled = await ServeAsync(context, itemId, signature, trace);
            trace.Result = handled ? "handled" : "passthrough";
            return handled;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { trace.Result = "client-cancelled"; throw; }
        catch (Exception e)
        { trace.Result = "failed-" + e.GetType().Name; throw; }
        finally
        {
            logger.LogInformation("Infuse chapters: request {RequestId}; item {ItemId}; {Method}; status {Status}; phase {Phase}; result {Result}; upstream HEAD {Upstream}; bytes {Bytes}; elapsed {Elapsed} ms.", context.TraceIdentifier, itemId, context.Request.Method, context.Response.StatusCode, trace.Phase, trace.Result, trace.UpstreamStatus, trace.Bytes, clock.ElapsedMilliseconds);
        }
    }

    private sealed class StreamTrace
    {
        internal string Phase = "configuration", Result = "incomplete";
        internal int UpstreamStatus;
        internal long Bytes;
    }

    private async Task<bool> ServeAsync(HttpContext context, string itemId, string signature, StreamTrace trace)
    {
        if (!config.EnableInfuseChapters) { context.Response.StatusCode = 410; return true; }
        // Authenticate every range request using the caller's Emby credentials,
        // including cache hits. Never substitute the management API key.
        var sourceId = context.Request.Query["MediaSourceId"].ToString();
        trace.Phase = "item-authorization";
        var query = "?Ids=" + Uri.EscapeDataString(itemId) + "&Fields=MediaSources,Chapters";
        foreach (var name in new[] { "api_key", "X-Emby-Token", "X-Emby-Authorization", "UserId" })
            if (!string.IsNullOrEmpty(context.Request.Query[name])) query += "&" + name + "=" + Uri.EscapeDataString(context.Request.Query[name].ToString());
        using var request = EmbyProxy.CreateRequest(context, new Uri(config.UpstreamBaseUrl.TrimEnd('/') + "/emby/Items" + query), "GET", false);
        request.Headers.Remove("Range"); request.Headers.AcceptEncoding.Clear(); request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        if (!response.IsSuccessStatusCode) { context.Response.StatusCode = (int)response.StatusCode; return true; }
        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: context.RequestAborted);
        DiscChapterPresentation? presentation = null;
        if (document.RootElement.TryGetProperty("Items", out var items)) foreach (var item in items.EnumerateArray())
            {
                if (item.GetProperty("Id").GetString() != itemId || !item.TryGetProperty("MediaSources", out var sources)) continue;
                foreach (var source in sources.EnumerateArray()) if (source.GetProperty("Id").GetString() == sourceId || sourceId.Length == 0 && sources.GetArrayLength() == 1) { presentation = Build(source); break; }
            }
        if (signature.Length == 0 && presentation is null) return false;
        if (presentation is null || signature.Length > 0 && !string.Equals(signature, presentation.Signature, StringComparison.Ordinal)) { context.Response.StatusCode = 409; return true; }
        signature = presentation.Signature;
        trace.Phase = "playback-authorization";
        // Item visibility and playback permission are distinct. Preserve Emby's
        // playback denials; only known broken BDMV 404/500 responses are bypassed.
        var canonical = new Uri(config.UpstreamBaseUrl.TrimEnd('/') + "/Videos/" + Uri.EscapeDataString(itemId) + "/stream" + context.Request.QueryString);
        using var headRequest = EmbyProxy.CreateRequest(context, canonical, "HEAD", false);
        EmbyProxy.PrepareMediaRequest(headRequest); headRequest.Headers.Remove("Range");
        using var head = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        trace.UpstreamStatus = (int)head.StatusCode;
        if (!head.IsSuccessStatusCode && head.StatusCode is not HttpStatusCode.NotFound and not HttpStatusCode.InternalServerError)
        { context.Response.StatusCode = (int)head.StatusCode; return true; }
        // Infuse can ignore DirectStreamUrl and construct /Videos/{id}/stream
        // itself. Give it an actual .iso URL before returning disc bytes, so
        // URL-based format selection cannot mistake the image for a TS stream.
        var streamPath = context.Request.Path.Value!.TrimEnd('/');
        if (!streamPath.EndsWith("/stream.iso", StringComparison.OrdinalIgnoreCase))
        {
            trace.Phase = "iso-redirect";
            var isoPath = streamPath[..(streamPath.LastIndexOf('/') + 1)] + "stream.iso";
            var isoQuery = context.Request.QueryString;
            if (context.Request.Query["HelperDisc"].Count == 0) isoQuery = isoQuery.Add("HelperDisc", signature);
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = context.Request.PathBase + isoPath + isoQuery;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = 0;
            return true;
        }
        var image = presentation.Image; long start = 0, end = image.Length - 1; var partial = false;
        trace.Phase = "range-validation";
        var range = context.Request.Headers.Range.ToString();
        var ifRange = context.Request.Headers.IfRange.ToString();
        if (ifRange.Length > 0 && ifRange != "\"" + signature + "\"") range = "";
        if (range.Length > 0)
        {
            if (!RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            { Unsatisfiable(context, image.Length); return true; }
            var part = parsed.Ranges.Single();
            if (!part.From.HasValue && part.To is not > 0 || part.From >= image.Length) { Unsatisfiable(context, image.Length); return true; }
            start = part.From ?? Math.Max(0, image.Length - part.To!.Value); end = part.From.HasValue ? Math.Min(part.To ?? end, end) : end; partial = true;
        }
        context.Response.StatusCode = partial ? 206 : 200; context.Response.ContentType = "video/iso";
        context.Response.ContentLength = end - start + 1; context.Response.Headers.AcceptRanges = "bytes"; context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ETag = "\"" + signature + "\""; context.Response.Headers.ContentDisposition = "inline; filename=EmbyBlurayHelper.iso";
        if (partial) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{image.Length}";
        trace.Phase = "image-stream";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            using var input = image.Open(); input.Position = start;
            var buffer = new byte[65536]; var remaining = end - start + 1;
            while (remaining > 0)
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (n == 0) throw new EndOfStreamException();
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, n), context.RequestAborted); remaining -= n; trace.Bytes += n;
            }
        }
        logger.LogInformation("Infuse chapters: virtual ISO item {ItemId}; {Method} range {Start}-{End}/{Length}; {Count} saved chapters; no remux.", itemId, context.Request.Method, start, end, image.Length, presentation.ChapterCount);
        trace.Phase = "complete";
        return true;
    }
    private static void Unsatisfiable(HttpContext context, long length)
    { context.Response.StatusCode = 416; context.Response.Headers.ContentRange = $"bytes */{length}"; context.Response.ContentLength = 0; }
}
