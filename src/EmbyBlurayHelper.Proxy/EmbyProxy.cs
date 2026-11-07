using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using EmbyBlurayHelper.Proxy.Chapters;
using EmbyBlurayHelper.Proxy.DolbyVision;
using EmbyBlurayHelper.Proxy.Matroska;

namespace EmbyBlurayHelper.Proxy;

internal sealed class EmbyProxy(ProxyConfiguration config, HttpClient client, IMemoryCache cache, ILogger<EmbyProxy> logger,
    Func<JsonElement, DiscChapterPresentation?>? chapterPresentationFactory = null, DiscListingStore? discListings = null, DvRuntime? dvRuntime = null)
{
    private readonly InfuseChapterProxy chapterProxy = new(config, client, cache, logger, chapterPresentationFactory, discListings);
    private readonly InfuseMatroskaProxy matroskaProxy = new(config, client, dvRuntime ?? new DvRuntime(DvOptions.From(config), logger), logger);
    private static readonly Regex StreamPath = new("^(?:/emby)?/videos/(?<id>[^/]+)/stream(?:\\.[a-z0-9]+)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlaybackReportPath = new("^(?:/emby)?/sessions/playing(?:/(?:progress|stopped))?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal const int MaxBufferedPlaybackReportBytes = 1024 * 1024;
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };

    public async Task HandleAsync(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest) { await ProxyWebSocketAsync(context); return; }
        var path = context.Request.Path.Value ?? "/";
        var match = StreamPath.Match(path);
        if (match.Success && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            await matroskaProxy.TryStreamAsync(context, match.Groups["id"].Value)) return;
        if (match.Success && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            await chapterProxy.TryStreamAsync(context, match.Groups["id"].Value)) return;
        if ((config.EnableInfuseLanguagePatch || config.EnableInfuseMultiClipPlayback) && match.Success && IsInfuse(context.Request) &&
            string.Equals(context.Request.Query["Static"], "true", StringComparison.OrdinalIgnoreCase) &&
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            var media = await GetMediaAsync(context, match.Groups["id"].Value);
            if (media is not null)
            {
                if (config.EnableInfuseMultiClipPlayback && media.Playlist is not null && await TryStreamPlaylistAsync(context, media)) return;
                if (config.EnableInfuseLanguagePatch && media.Languages is not null && await TryStreamPatchedAsync(context, media.Languages, media.SourcePath)) return;
            }
        }
        using var request = CreateRequest(context, Target(context), context.Request.Method, true);
        if (request.Content is not null && context.Request.ContentLength is null && IsPlaybackReport(context.Request) &&
            !await BufferPlaybackReportAsync(context, request)) return;
        var chapterMetadata = chapterProxy.IsMetadata(context);
        var dolbyMetadata = matroskaProxy.IsMetadata(context);
        if (chapterMetadata || dolbyMetadata) { request.Headers.AcceptEncoding.Clear(); request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity")); }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        CopyHeaders(response, context.Response);
        if ((chapterMetadata || dolbyMetadata) && response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json" && !HttpMethods.IsHead(context.Request.Method))
        {
            var original = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            var changed = dolbyMetadata ? await matroskaProxy.RewriteMetadataAsync(context, original) : null;
            var chapterChanged = chapterMetadata ? await chapterProxy.RewriteMetadataAsync(context, changed ?? original) : null;
            var output = chapterChanged ?? changed ?? original;
            if (changed is not null || chapterChanged is not null) { context.Response.Headers.Remove("ETag"); context.Response.Headers.Remove("Content-Encoding"); context.Response.Headers.Remove("Content-MD5"); context.Response.Headers.CacheControl = "no-store"; }
            context.Response.ContentLength = output.Length;
            await context.Response.Body.WriteAsync(output, context.RequestAborted);
            return;
        }
        if (chapterMetadata) chapterProxy.LogUnchanged(context, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType);
        if (!HttpMethods.IsHead(context.Request.Method))
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private async Task<BlurayMedia?> GetMediaAsync(HttpContext context, string itemId)
    {
        // Never use or log raw credentials as cache keys. Media bytes are still
        // authenticated by Emby on every request, including cache hits.
        var credential = string.Join('|', context.Request.Headers["X-Emby-Token"].ToString(),
            context.Request.Headers["X-Emby-Authorization"].ToString(), context.Request.Headers.Authorization.ToString(),
            context.Request.Query["api_key"].ToString(), context.Request.Query["X-Emby-Token"].ToString(),
            context.Request.Query["X-Emby-Authorization"].ToString());
        var sourceId = context.Request.Query["MediaSourceId"].ToString();
        var key = "languages:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))) + ":" + itemId + ":" + sourceId;
        BlurayMedia? media = null;
        try
        {
            var query = "?Ids=" + Uri.EscapeDataString(itemId) + "&Fields=MediaSources,MediaStreams";
            foreach (var name in new[] { "api_key", "X-Emby-Token", "X-Emby-Authorization", "UserId" })
                if (!string.IsNullOrEmpty(context.Request.Query[name]))
                    query += "&" + name + "=" + Uri.EscapeDataString(context.Request.Query[name].ToString());
            using var request = CreateRequest(context, new Uri(config.UpstreamBaseUrl.TrimEnd('/') + "/emby/Items" + query), "GET", false);
            request.Headers.Remove("Range");
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (!response.IsSuccessStatusCode) return null;
            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: context.RequestAborted);
            if (document.RootElement.TryGetProperty("Items", out var items))
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("Id", out var id) || id.GetString() != itemId) continue;
                    if (!item.TryGetProperty("MediaSources", out var sources)) continue;
                    foreach (var source in sources.EnumerateArray())
                    {
                        if (sourceId.Length > 0 && source.GetProperty("Id").GetString() != sourceId) continue;
                        // Check that Emby still exposes this item and source to
                        // the caller before using cached local disc metadata.
                        if (cache.TryGetValue<CachedMedia>(key, out var cached)) return cached!.Media;
                        media = BlurayLanguageMap.Read(source, logger);
                        break;
                    }
                }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Unable to read UHD language metadata for item {ItemId} ({ErrorType}); forwarding unchanged.", itemId, exception.GetType().Name);
        }
        cache.Set(key, new CachedMedia(media), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        return media;
    }

    private async Task<bool> TryStreamPlaylistAsync(HttpContext context, BlurayMedia media)
    {
        // A real upstream media request still enforces Emby's playback policy.
        // Only replace its broken multi-clip static-file response.
        using var headRequest = CreateRequest(context, Target(context), "HEAD", false);
        PrepareMediaRequest(headRequest);
        headRequest.Headers.Remove("Range");
        using var head = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        if (head.StatusCode is not System.Net.HttpStatusCode.NotFound and not System.Net.HttpStatusCode.InternalServerError) return false;
        var title = media.Playlist!;
        try
        {
            var planKey = "whole-title:" + title.Root + ":" + title.Number;
            if (!cache.TryGetValue<WholeClipPlan>(planKey, out var plan))
            {
                var managed = WholeClipPlaylist.TryCreate(title.Root, title.Number, out var reason);
                plan = new(managed, reason);
                cache.Set(planKey, plan, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
            }
            if (plan!.Playlist is null) throw new IOException(plan.Reason);
            var total = plan.Playlist.Length;
            long start = 0, end = total - 1;
            var partial = false;
            var originalRange = context.Request.Headers.Range.ToString();
            if (originalRange.Length > 0)
            {
                if (!RangeHeaderValue.TryParse(originalRange, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1) return false;
                var part = range.Ranges.Single();
                if (!part.From.HasValue && part.To is not > 0 || part.From >= total)
                {
                    context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    context.Response.Headers.ContentRange = $"bytes */{total}";
                    context.Response.ContentLength = 0;
                    return true;
                }
                start = part.From ?? Math.Max(0, total - part.To!.Value);
                end = part.From.HasValue ? Math.Min(part.To ?? end, end) : end;
                partial = true;
            }
            var length = end - start + 1;
            var upstreamStart = start / TransportLanguagePatcher.BlockSize * TransportLanguagePatcher.BlockSize;
            // Validate read access before committing successful headers.
            await using var input = HttpMethods.IsHead(context.Request.Method) ? null : plan.Playlist.Open(upstreamStart);
            context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            context.Response.ContentType = "video/mp2t";
            context.Response.ContentLength = length;
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentDisposition = VirtualMediaName.MultiClip(title.Root).ContentDisposition;
            if (partial) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{total}";
            if (input is not null)
            {
                var stats = await TransportLanguagePatcher.CopyAsync(input, context.Response.Body, upstreamStart, start, length,
                    config.EnableInfuseLanguagePatch ? media.Languages ?? new Dictionary<ushort, string>() : new Dictionary<ushort, string>(), context.RequestAborted);
                logger.LogInformation("Infuse multi-clip Blu-ray: built-in MPLS/CLPI reader; playlist {Playlist}; byte range {Start}-{End}/{Total}; {Patched} PMTs updated; {NoSpace} PMTs lacked stuffing space; {Length} bytes served through memory pipe.",
                    title.Number, start, end, total, stats.Patched, stats.NoSpace, length);
            }
            return true;
        }
        catch (Exception exception) when (!context.Response.HasStarted && exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException or OverflowException)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            context.Response.Clear();
            logger.LogWarning("Infuse multi-clip Blu-ray reader failed ({ErrorType}): {Reason}; forwarding Emby's response.", exception.GetType().Name, exception.Message);
            return false;
        }
    }

    private sealed record WholeClipPlan(WholeClipPlaylist? Playlist, string Reason);

    private async Task<bool> TryStreamPatchedAsync(HttpContext context, IReadOnlyDictionary<ushort, string> languages, string sourcePath)
    {
        var target = Target(context);
        long visibleStart = 0, upstreamStart = 0, visibleLength = 0, totalLength = 0;
        string? expandedRange = null;
        var originalRange = context.Request.Headers.Range.ToString();
        if (originalRange.Length > 0)
        {
            if (!RangeHeaderValue.TryParse(originalRange, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1) return false;
            using var headRequest = CreateRequest(context, target, "HEAD", false);
            PrepareMediaRequest(headRequest);
            headRequest.Headers.Remove("Range");
            using var head = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (!head.IsSuccessStatusCode || head.Content.Headers.ContentLength is not > 0) return false;
            totalLength = head.Content.Headers.ContentLength.Value;
            var part = range.Ranges.Single();
            if (!part.From.HasValue && part.To is not > 0) return false;
            visibleStart = part.From ?? Math.Max(0, totalLength - part.To!.Value);
            var visibleEnd = part.From.HasValue ? Math.Min(part.To ?? (totalLength - 1), totalLength - 1) : totalLength - 1;
            if (visibleStart > visibleEnd || visibleStart >= totalLength) return false;
            visibleLength = visibleEnd - visibleStart + 1;
            upstreamStart = visibleStart / TransportLanguagePatcher.BlockSize * TransportLanguagePatcher.BlockSize;
            var upstreamEnd = Math.Min(totalLength - 1,
                (visibleEnd / TransportLanguagePatcher.BlockSize + 1) * TransportLanguagePatcher.BlockSize - 1);
            expandedRange = $"bytes={upstreamStart}-{upstreamEnd}";
        }
        using var request = CreateRequest(context, target, context.Request.Method, false);
        PrepareMediaRequest(request);
        request.Headers.Remove("Range");
        if (expandedRange is not null) request.Headers.TryAddWithoutValidation("Range", expandedRange);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        if (!response.IsSuccessStatusCode) { CopyHeaders(response, context.Response); await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted); return true; }
        if (response.Content.Headers.ContentEncoding.Count != 0) return false;
        if (expandedRange is not null)
        {
            var actualRange = response.Content.Headers.ContentRange;
            if (actualRange?.From != upstreamStart || actualRange.Length != totalLength ||
                actualRange.To < visibleStart + visibleLength - 1 || actualRange.To != long.Parse(expandedRange.Split('-')[1])) return false;
        }
        else
        {
            if (response.Content.Headers.ContentLength is not > 0) return false;
            visibleLength = response.Content.Headers.ContentLength.Value;
        }
        CopyHeaders(response, context.Response);
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Content-MD5");
        context.Response.Headers.Remove("Digest");
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentDisposition = VirtualMediaName.Language(sourcePath).ContentDisposition;
        context.Response.ContentLength = visibleLength;
        if (expandedRange is not null)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {visibleStart}-{visibleStart + visibleLength - 1}/{totalLength}";
        }
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var stats = await TransportLanguagePatcher.CopyAsync(input, context.Response.Body, upstreamStart, visibleStart, visibleLength, languages, context.RequestAborted);
            logger.LogInformation("Infuse UHD stream patch: {Patched} PMTs updated; {NoSpace} PMTs lacked stuffing space; {Length} bytes served.", stats.Patched, stats.NoSpace, visibleLength);
        }
        return true;
    }

    internal static void PrepareMediaRequest(HttpRequestMessage request)
    {
        foreach (var name in new[] { "If-Range", "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since" }) request.Headers.Remove(name);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
    }

    internal static bool IsInfuse(HttpRequest request)
    {
        if (request.Headers.UserAgent.Any(v => v?.Contains("Infuse", StringComparison.OrdinalIgnoreCase) == true)) return true;
        foreach (var name in new[] { "X-Emby-Authorization", "X-Emby-Client" })
            if (request.Headers[name].Any(v => v?.Contains("Infuse", StringComparison.OrdinalIgnoreCase) == true) ||
                request.Query[name].Any(v => v?.Contains("Infuse", StringComparison.OrdinalIgnoreCase) == true)) return true;
        return false;
    }

    private Uri Target(HttpContext context) => new(config.UpstreamBaseUrl.TrimEnd('/') + context.Request.Path + context.Request.QueryString);

    private static bool IsPlaybackReport(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && PlaybackReportPath.IsMatch(request.Path.Value ?? "");

    private async Task<bool> BufferPlaybackReportAsync(HttpContext context, HttpRequestMessage request)
    {
        // Emby 4.10's request binder can ignore chunked playback JSON. Only
        // lengthless playback reports are buffered; media/uploads keep streaming.
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, MaxBufferedPlaybackReportBytes + 1 - (int)body.Length)), context.RequestAborted);
            if (read == 0) break;
            if (body.Length + read > MaxBufferedPlaybackReportBytes)
            {
                logger.LogWarning("Playback report exceeded the {Limit} byte buffering limit; request was not forwarded.", MaxBufferedPlaybackReportBytes);
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentLength = 0;
                return false;
            }
            body.Write(buffer, 0, read);
        }
        var original = request.Content!;
        var content = new ByteArrayContent(body.GetBuffer(), 0, (int)body.Length);
        foreach (var header in original.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Content = content;
        original.Dispose();
        return true;
    }

    internal static HttpRequestMessage CreateRequest(HttpContext context, Uri uri, string method, bool copyBody)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (copyBody && (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            context.Request.ContentLength is null && IsPlaybackReport(context.Request)))
        {
            request.Content = new StreamContent(context.Request.Body);
            // StreamContent cannot infer a Kestrel request stream's length.
            // Preserve the validated inbound framing instead of switching to chunked.
            if (context.Request.ContentLength is long length) request.Content.Headers.ContentLength = length;
        }
        foreach (var header in context.Request.Headers)
        {
            if (HopByHop.Contains(header.Key) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null)
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        return request;
    }

    private static void CopyHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
            if (!HopByHop.Contains(header.Key)) response.Headers[header.Key] = header.Value.ToArray();
    }

    private async Task ProxyWebSocketAsync(HttpContext context)
    {
        var uri = new UriBuilder(Target(context)) { Scheme = config.UpstreamBaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws" };
        using var upstream = new ClientWebSocket();
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) upstream.Options.AddSubProtocol(protocol);
        foreach (var header in context.Request.Headers)
            if (!HopByHop.Contains(header.Key) && !header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                upstream.Options.SetRequestHeader(header.Key, header.Value.ToString());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            await upstream.ConnectAsync(uri.Uri, cancellation.Token);
            using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
            var toServer = RelayAsync(downstream, upstream, cancellation.Token);
            var toClient = RelayAsync(upstream, downstream, cancellation.Token);
            await Task.WhenAny(toServer, toClient);
            cancellation.Cancel();
            await Task.WhenAll(toServer, toClient);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    private static async Task RelayAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && source.State == WebSocketState.Open)
        {
            var message = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (message.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(message.CloseStatus ?? WebSocketCloseStatus.NormalClosure, message.CloseStatusDescription, cancellationToken);
                break;
            }
            await destination.SendAsync(new ArraySegment<byte>(buffer, 0, message.Count), message.MessageType, message.EndOfMessage, cancellationToken);
        }
    }

    private sealed record CachedMedia(BlurayMedia? Media);
}
