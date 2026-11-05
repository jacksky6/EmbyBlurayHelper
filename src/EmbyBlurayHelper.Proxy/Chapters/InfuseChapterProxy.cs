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
    Func<JsonElement, DiscChapterPresentation?>? presentationFactory = null, DiscListingStore? listingStore = null)
{
    private readonly DiscListingStore listings = listingStore ?? new(null, logger);
    // Shared by all concurrent library pages: at most four discs are read at once.
    private readonly SemaphoreSlim throttle = new(4);
    private static readonly Regex MetadataRoute = new("^(?:/emby)?/(?:Items/[^/]+/PlaybackInfo|(?:Users/[^/]+/)?Items(?:/[^/]+)?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal bool IsMetadata(HttpContext context) => config.EnableInfuseChapters && EmbyProxy.IsInfuse(context.Request) && MetadataRoute.IsMatch(context.Request.Path);

    // Infuse keeps the item format from whichever list or detail response it
    // synced last, and those responses carry different fields. Decide every
    // disc item from the same Emby lookup the stream route uses, never from
    // whether this particular response happened to include sources or chapters.
    internal async Task<byte[]?> RewriteMetadataAsync(HttpContext context, byte[] bytes)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (bytes.Length > 8 * 1024 * 1024) { LogMetadata(context, "skipped-too-large", 0, [], [], clock); return null; }
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); } catch (JsonException) { LogMetadata(context, "skipped-not-json", 0, [], [], clock); return null; }
        var items = root switch
        {
            JsonObject obj when obj["Items"] is JsonArray list => list.OfType<JsonObject>().ToList(),
            JsonObject obj => [obj],
            JsonArray list => list.OfType<JsonObject>().ToList(),
            _ => []
        };
        var candidates = new List<(JsonObject Item, string Id)>();
        foreach (var item in items)
        {
            var id = Text(item["Id"]) ?? Regex.Match(context.Request.Path, "(?i)/Items/([^/]+)").Groups[1].Value;
            var sources = item["MediaSources"] as JsonArray;
            var disc = string.Equals(Text(item["Container"]), "bluray", StringComparison.OrdinalIgnoreCase) ||
                sources?.OfType<JsonObject>().Any(s => string.Equals(Text(s["Container"]), "bluray", StringComparison.OrdinalIgnoreCase)) == true;
            if (disc && !string.IsNullOrEmpty(id)) candidates.Add((item, id));
        }
        var rewritten = new List<string>(); var unresolved = new List<string>();
        if (candidates.Count > 0)
        {
            var canonical = await CanonicalSourcesAsync(context, candidates.Select(c => c.Id).Distinct().ToArray());
            // A per-item line only for single-item responses; library pages are
            // summarized by LogMetadata, so a NAS-sized sync does not flood the log.
            foreach (var (item, id) in candidates)
                if (canonical is not null && canonical.TryGetValue(id, out var known) && RewriteItem(context, item, id, known, items.Count == 1)) rewritten.Add(id);
                else unresolved.Add(id);
        }
        LogMetadata(context, rewritten.Count > 0 ? "rewritten" : "unchanged", items.Count, rewritten, unresolved, clock);
        return rewritten.Count > 0 ? Encoding.UTF8.GetBytes(root!.ToJsonString()) : null;
    }

    private bool RewriteItem(HttpContext context, JsonObject obj, string itemId, IReadOnlyDictionary<string, (DiscListingStore.Listing? Listing, string Path)> known, bool logDetail)
    {
        var changed = false;
        foreach (var source in (obj["MediaSources"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var sourceId = Text(source["Id"]);
            if (sourceId is null || !known.TryGetValue(sourceId, out var match) || match.Listing is null) continue;
            source["Container"] = "blurayiso"; source["Size"] = match.Listing.Length; source["Path"] = IsoPath(match.Path);
            // Preserve native media-source hints. Infuse can select a different
            // reader when extra URL/MIME hints are injected into the DTO. The
            // authenticated stream route supplies the explicit ISO redirect.
            source["SupportsDirectPlay"] = true; source["SupportsDirectStream"] = true;
            changed = true;
        }
        // Infuse can retain item-level information from a library listing.
        // Describe one consistent file at both levels, rather than an ISO
        // media source inside an item still advertised as a BDMV folder.
        if (known.Count == 1 && known.Values.Single() is { Listing: { } listing } single)
        {
            if (obj.ContainsKey("Container")) obj["Container"] = "blurayiso";
            if (Text(obj["Path"]) == single.Path) obj["Path"] = IsoPath(single.Path);
            if (obj.ContainsKey("Size")) obj["Size"] = listing.Length;
            changed = true;
            if (logDetail) logger.LogInformation("Infuse chapters: item {ItemId}; {Count} saved Emby chapters in virtual ISO, playlist {Playlist}, navigation {Bytes} bytes; original media is read on demand; metadata route {Route}.", itemId, listing.ChapterCount, listing.Playlist, listing.MetadataBytes, context.Request.Path.Value);
        }
        return changed;
    }

    private async Task<Dictionary<string, Dictionary<string, (DiscListingStore.Listing? Listing, string Path)>>?> CanonicalSourcesAsync(HttpContext context, string[] ids)
    {
        try
        {
            using var request = EmbyProxy.CreateRequest(context, new Uri(config.UpstreamBaseUrl.TrimEnd('/') + "/emby/Items?Ids=" + Uri.EscapeDataString(string.Join(',', ids)) + "&Fields=MediaSources,Chapters" + CallerQuery(context)), "GET", false);
            request.Headers.Remove("Range"); request.Headers.AcceptEncoding.Clear(); request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (!response.IsSuccessStatusCode) { logger.LogWarning("Infuse chapters: catalog lookup returned {Status}; forwarding original metadata.", (int)response.StatusCode); return null; }
            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: context.RequestAborted);
            var builds = new List<(string Item, string Source, string Path, Task<DiscListingStore.Listing?> Task)>();
            if (!document.RootElement.TryGetProperty("Items", out var list)) return [];
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("Id", out var itemId) && item.TryGetProperty("MediaSources", out var sources))
                    foreach (var source in sources.EnumerateArray())
                    {
                        if (!source.TryGetProperty("Id", out var sourceId)) continue;
                        var copy = source.Clone();
                        var path = copy.TryGetProperty("Path", out var value) ? value.GetString() ?? "" : "";
                        // Known items are answered from the record without a disk read.
                        var task = listings.TryGet(DiscListingStore.Key(copy), out var known) ? Task.FromResult(known) : Task.Run(async () =>
                        {
                            await throttle.WaitAsync(context.RequestAborted);
                            try { return Describe(copy); } finally { throttle.Release(); }
                        });
                        builds.Add((itemId.GetString()!, sourceId.GetString()!, path, task));
                    }
            }
            try { await Task.WhenAll(builds.Select(b => b.Task)); }
            finally { listings.Flush(); }
            return builds.GroupBy(b => b.Item).ToDictionary(g => g.Key, g => g.ToDictionary(b => b.Source, b => (b.Task.Result, b.Path)));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or ArgumentException ||
            e is TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Infuse chapters: catalog lookup failed ({ErrorType}); forwarding original metadata.", e.GetType().Name);
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string IsoPath(string source) => source.TrimEnd('/', '\\') + (source.Contains('\\') ? '\\' : '/') + "EmbyBlurayHelper.iso";

    // Upstream checks run with the caller's own Emby credentials, never the
    // management API key.
    private static string CallerQuery(HttpContext context)
    {
        var query = "";
        foreach (var name in new[] { "api_key", "X-Emby-Token", "X-Emby-Authorization", "UserId" })
            if (!string.IsNullOrEmpty(context.Request.Query[name])) query += "&" + name + "=" + Uri.EscapeDataString(context.Request.Query[name].ToString());
        return query;
    }

    // One diagnostic line per Infuse metadata response. Only route, query key
    // names and the Fields list are logged; never tokens or other values.
    private void LogMetadata(HttpContext context, string result, int items, List<string> rewritten, List<string> unresolved, System.Diagnostics.Stopwatch clock) =>
        logger.LogInformation("Infuse metadata: {Method} {Route}; query keys {Keys}; page {Start}+{Limit}; fields {Fields}; result {Result}; {Items} items; disc items rewritten [{Rewritten}]; unresolved [{Unresolved}]; elapsed {Elapsed} ms.",
            context.Request.Method, context.Request.Path.Value, QueryKeys(context), Number(context, "StartIndex"), Number(context, "Limit"), Fields(context), result, items, string.Join(",", rewritten.Take(20)), string.Join(",", unresolved.Take(20)), clock.ElapsedMilliseconds);

    private static string Number(HttpContext context, string name) => int.TryParse(context.Request.Query[name].ToString(), out var value) ? value.ToString() : "-";

    internal void LogUnchanged(HttpContext context, int status, string? contentType) =>
        logger.LogInformation("Infuse metadata: {Method} {Route}; query keys {Keys}; fields {Fields}; result not-rewritable; status {Status}; type {Type}.",
            context.Request.Method, context.Request.Path.Value, QueryKeys(context), Fields(context), status, contentType ?? "none");

    private static string QueryKeys(HttpContext context) => string.Join(",", context.Request.Query.Keys.Where(k => Regex.IsMatch(k, "^[A-Za-z0-9_.-]{1,40}$")).Order(StringComparer.OrdinalIgnoreCase));
    private static string Fields(HttpContext context)
    {
        var fields = context.Request.Query["Fields"].ToString();
        return fields.Length == 0 ? "none" : Regex.IsMatch(fields, "^[A-Za-z0-9,]{1,400}$") ? fields : "unrecognized";
    }

    private DiscChapterPresentation? Build(JsonElement source)
    {
        var key = "chapter-disc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.GetRawText())));
        if (cache.TryGetValue<DiscChapterPresentation>(key, out var cached) && cached!.IsCurrent) return cached;
        var (presentation, definite) = TryCreate(source);
        if (presentation is not null) cache.Set(key, presentation, new MemoryCacheEntryOptions { Size = Math.Max(1, (presentation.Image.MetadataBytes + 1048575) / 1048576), AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
        // Playback reads the disc anyway; keep the listing record in step with it.
        if (definite) { listings.Put(DiscListingStore.Key(source), ToListing(presentation)); listings.Flush(); }
        return presentation;
    }

    // Library syncs only consult the listing record. The disc itself is read
    // once per Emby record, never on every sync.
    private DiscListingStore.Listing? Describe(JsonElement source)
    {
        var key = DiscListingStore.Key(source);
        if (listings.TryGet(key, out var known)) return known;
        var (presentation, definite) = TryCreate(source);
        if (definite) listings.Put(key, ToListing(presentation)); else listings.Defer(key);
        return ToListing(presentation);
    }

    private static DiscListingStore.Listing? ToListing(DiscChapterPresentation? p) =>
        p is null ? null : new(p.Image.Length, p.ChapterCount, p.Playlist, p.Image.MetadataBytes);

    // Definite results are properties of the disc and Emby's record. I/O and
    // permission failures (an unmounted or sleeping-and-failing volume) are not.
    private (DiscChapterPresentation? Presentation, bool Definite) TryCreate(JsonElement source)
    {
        try
        {
            return (presentationFactory is null ? DiscChapterPresentation.Create(source) : presentationFactory(source), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Infuse chapters: disc temporarily unreadable ({ErrorType}); forwarding original playback information.", e.GetType().Name);
            return (null, false);
        }
        // InvalidDataException carries "keep the original playback" decisions
        // (no matching title, ambiguous branches); it must not fail the request.
        catch (Exception e) when (e is ArgumentException or OverflowException or JsonException or IndexOutOfRangeException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Infuse chapters: presentation unavailable ({ErrorType}); forwarding original playback information.", e.GetType().Name);
            return (null, true);
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
        var query = "?Ids=" + Uri.EscapeDataString(itemId) + "&Fields=MediaSources,Chapters" + CallerQuery(context);
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
