using System.Net;
using System.Text;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.Proxy.DolbyVision;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PlaybackReportChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var json = Encoding.UTF8.GetBytes("{\"PlaySessionId\":\"existing-session\",\"ItemId\":\"12\",\"PositionTicks\":123456789,\"IsPaused\":true,\"Extra\":{\"Text\":\"中文字幕\"}}");
        foreach (var prefix in new[] { "", "/EmBy" })
        foreach (var suffix in new[] { "", "/Progress", "/Stopped" })
        foreach (var framing in new[] { "length", "chunked", "http2-lengthless" })
        {
            var route = prefix + "/Sessions/Playing" + suffix + "/";
            var result = await Call(route, json, framing);
            var label = route + " " + framing;
            check(!result.Cancelled && result.Context.Response.StatusCode == 204 && result.Handler.Calls == 1, "playback report reaches upstream: " + label);
            check(result.Handler.Length == json.Length && result.Handler.Bytes!.SequenceEqual(json), "playback report has explicit byte length and unchanged JSON: " + label);
            check(result.Handler.RequestUri?.PathAndQuery == route + "?UserId=caller&existing=value" &&
                result.Handler.ContentType == "application/json; charset=utf-8" && result.Handler.CallerPreserved,
                "playback report preserves route, content type and caller: " + label);
            check(result.Handler.ReadBeforeSend == (framing == "length" ? 0 : json.Length), "only lengthless playback reports are buffered: " + label);
        }

        var boundary = await Call("/Sessions/Playing", new byte[EmbyProxy.MaxBufferedPlaybackReportBytes], "chunked");
        check(boundary.Handler.Calls == 1 && boundary.Handler.Length == EmbyProxy.MaxBufferedPlaybackReportBytes,
            "lengthless playback report accepts the exact buffer limit");
        var oversized = await Call("/Sessions/Playing", new byte[EmbyProxy.MaxBufferedPlaybackReportBytes + 17], "chunked");
        check(oversized.Context.Response.StatusCode == 413 && oversized.Context.Response.ContentLength == 0 && oversized.Handler.Calls == 0 &&
            oversized.Source.ReadBytes == EmbyProxy.MaxBufferedPlaybackReportBytes + 1,
            "oversized lengthless report stops at limit plus one byte and never reaches upstream");
        var largeKnown = await Call("/Sessions/Playing", new byte[EmbyProxy.MaxBufferedPlaybackReportBytes + 17], "length");
        check(largeKnown.Handler.Calls == 1 && largeKnown.Handler.ReadBeforeSend == 0 && largeKnown.Handler.Length == EmbyProxy.MaxBufferedPlaybackReportBytes + 17,
            "known-length requests remain streaming without the playback buffer limit");

        foreach (var route in new[] { "/upload", "/Sessions/Playing/Other", "/Other/Sessions/Playing" })
        {
            var other = await Call(route, json, "chunked");
            check(other.Handler.Length is null && other.Handler.ReadBeforeSend == 0 && other.Handler.Bytes!.SequenceEqual(json),
                "unrelated lengthless request is still streamed: " + route);
        }
        var otherMethod = await Call("/Sessions/Playing", json, "chunked", method: "PUT");
        check(otherMethod.Handler.ReadBeforeSend == 0 && otherMethod.Handler.Length is null, "non-POST requests are not playback-report buffered");
        var empty = await Call("/Sessions/Playing", [], "chunked");
        check(empty.Handler.Length == 0 && empty.Handler.Bytes!.Length == 0, "empty lengthless report stays empty without invented fields");
        var rejected = await Call("/Sessions/Playing", Encoding.UTF8.GetBytes("{invalid json"), "chunked", upstreamStatus: HttpStatusCode.BadRequest);
        check(rejected.Context.Response.StatusCode == 400 && rejected.Handler.Bytes!.SequenceEqual(Encoding.UTF8.GetBytes("{invalid json")) &&
            ((MemoryStream)rejected.Context.Response.Body).ToArray().SequenceEqual(Encoding.UTF8.GetBytes("upstream-error")),
            "malformed JSON and upstream errors are not rewritten or hidden");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await Call("/Sessions/Playing", json, "chunked", token: cancellation.Token);
        check(cancelled.Cancelled && cancelled.Handler.Calls == 0, "cancelled report buffering never forwards partial JSON");

        var lookup = new DefaultHttpContext(); lookup.Request.Method = "POST"; lookup.Request.ContentLength = json.Length;
        lookup.Request.Body = new NonSeekableBody(json);
        using var request = EmbyProxy.CreateRequest(lookup, new Uri("http://upstream/lookup"), "GET", false);
        check(request.Content is null, "body-free upstream media lookups do not acquire playback request content");
    }

    private static async Task<(DefaultHttpContext Context, NonSeekableBody Source, Capture Handler, bool Cancelled)> Call(
        string path, byte[] bytes, string framing, string method = "POST", HttpStatusCode upstreamStatus = HttpStatusCode.NoContent, CancellationToken token = default)
    {
        var context = new DefaultHttpContext(); context.Request.Method = method; context.Request.Path = path;
        context.Request.QueryString = new QueryString("?UserId=caller&existing=value");
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Request.Headers.UserAgent = "Infuse-Direct/8.5.9";
        context.Request.Headers["X-Emby-Token"] = "caller-fixture-token";
        if (framing == "length") context.Request.ContentLength = bytes.Length;
        else if (framing == "chunked") context.Request.Headers.TransferEncoding = "chunked";
        else context.Request.Protocol = "HTTP/2";
        var source = new NonSeekableBody(bytes); context.Request.Body = source;
        context.RequestAborted = token; context.Response.Body = new MemoryStream();
        var capture = new Capture(source, upstreamStatus);
        using var client = new HttpClient(capture); using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 8 });
        var config = new ProxyConfiguration { UpstreamBaseUrl = "http://upstream", EnableInfuseLanguagePatch = false };
        using var runtime = new DvRuntime(DvOptions.From(config), NullLogger<EmbyProxy>.Instance);
        var proxy = new EmbyProxy(config, client, memory, NullLogger<EmbyProxy>.Instance, dvRuntime: runtime);
        var cancelled = false;
        try { await proxy.HandleAsync(context); } catch (OperationCanceledException) { cancelled = true; }
        return (context, source, capture, cancelled);
    }

    private sealed class Capture(NonSeekableBody source, HttpStatusCode status) : HttpMessageHandler
    {
        internal int Calls, ReadBeforeSend;
        internal long? Length;
        internal byte[]? Bytes;
        internal Uri? RequestUri;
        internal string? ContentType;
        internal bool CallerPreserved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; ReadBeforeSend = source.ReadBytes;
            Length = request.Content?.Headers.ContentLength; RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            CallerPreserved = request.Headers.GetValues("X-Emby-Token").Single() == "caller-fixture-token" &&
                request.Headers.UserAgent.ToString() == "Infuse-Direct/8.5.9" && !request.Headers.Contains("Transfer-Encoding");
            Bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(token);
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(status == HttpStatusCode.BadRequest ? Encoding.UTF8.GetBytes("upstream-error") : []) };
        }
    }

    private sealed class NonSeekableBody(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        internal int ReadBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); ReadBytes += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer.Span); ReadBytes += read; return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
