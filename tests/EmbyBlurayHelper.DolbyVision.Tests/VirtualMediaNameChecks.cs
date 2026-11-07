using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.Proxy.Chapters;
using EmbyBlurayHelper.Proxy.DolbyVision;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

internal static class VirtualMediaNameChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        const string release = "少数派报告.Minority.Report.2002.2160p.DTS-HD.MA5.1-sh@CHDBits";
        foreach (var root in new[] { @"\\nas\share\" + release, @"D:\Movies\" + release, "/volume1/movies/" + release, "/media/" + release })
        foreach (var tail in new[] { "", "/", "\\", "/BDMV", "/BDMV/index.bdmv" })
        {
            var named = VirtualMediaName.Chapters(root + tail);
            check(named.FileName == release + ".EmbyBlurayHelper.Chapters.iso", "chapter ISO preserves the full dotted release directory and group: " + root + tail);
            var parsed = ContentDispositionHeaderValue.Parse(named.ContentDisposition);
            check(parsed.FileNameStar == named.FileName && named.ContentDisposition.All(c => c is >= ' ' and <= '~'),
                "HTTP filename is complete Unicode with ASCII-only transport");
            check(named.Path.EndsWith(named.FileName, StringComparison.Ordinal) && named.Path[..^named.FileName.Length].TrimEnd('/', '\\') == root,
                "chapter virtual path retains the original release directory");
        }
        foreach (var path in new[] { @"\\nas\share\禁闭岛.2010.2160p-AQLJ@HDSky.ISO", @"D:\Movies\禁闭岛.2010.2160p-AQLJ@HDSky.iso", "/media/禁闭岛.2010.2160p-AQLJ@HDSky.iso", "禁闭岛.2010.2160p-AQLJ@HDSky.iso" })
        {
            var named = VirtualMediaName.DolbyVision(path);
            check(named.FileName == "禁闭岛.2010.2160p-AQLJ@HDSky.EmbyBlurayHelper.P81.mkv" && !named.Path.Contains(".iso/", StringComparison.OrdinalIgnoreCase),
                "ISO-derived MKV keeps the file stem and group, replacing only the ISO extension");
            check(ContentDispositionHeaderValue.Parse(named.ContentDisposition).FileNameStar == named.FileName,
                "ISO-derived MKV has a matching HTTP filename");
        }
        check(VirtualMediaName.MultiClip("/media/" + release).FileName == release + ".EmbyBlurayHelper.MultiClip.m2ts" &&
            VirtualMediaName.Language("/media/" + release).FileName == release + ".EmbyBlurayHelper.Language.m2ts",
            "multi-clip and language-patched streams also retain release information");
        var longRelease = new string('a', 280) + "-GROUP@CHDBits";
        check(VirtualMediaName.DolbyVision("/media/" + longRelease).FileName.StartsWith(longRelease, StringComparison.Ordinal), "long release names never lose their final group suffix");
        var special = VirtualMediaName.Chapters("/media/中文 \"title\";name\r\nHeader");
        check(!special.ContentDisposition.Contains('\r') && !special.ContentDisposition.Contains('\n') &&
            ContentDispositionHeaderValue.Parse(special.ContentDisposition).FileNameStar == special.FileName, "quotes, separators and control characters cannot inject HTTP headers");
        check(VirtualMediaName.Chapters("").FileName == "Bluray.EmbyBlurayHelper.Chapters.iso", "missing source name has a deterministic fallback");

        var image = VirtualUdfImage.Create([new("BDMV/index.bdmv", null, 4, [1, 2, 3, 4])]);
        var fixture = JsonSerializer.SerializeToUtf8Bytes(new { Items = new[] { new { Id = "12", Name = "Original movie title", Container = "bluray", Path = @"\\nas\share\" + release,
            MediaSources = new[] { new { Id = "source", Name = "Old source label", Container = "bluray", Path = @"\\nas\share\" + release, RunTimeTicks = 1000000000L, Chapters = new[] { new { StartPositionTicks = 0L } } } } } } });
        var config = new ProxyConfiguration { UpstreamBaseUrl = "http://fixture", EnableInfuseLanguagePatch = false, EnableInfuseChapters = true };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
        using var http = new HttpClient(new Catalog(fixture, image.Length));
        using var runtime = new DvRuntime(DvOptions.From(config), NullLogger<EmbyProxy>.Instance);
        var proxy = new EmbyProxy(config, http, cache, NullLogger<EmbyProxy>.Instance, _ => new(image, "fixture-signature", "00001.mpls", 1), dvRuntime: runtime);
        async Task<DefaultHttpContext> Call(string method, string route, string query = "", string agent = "Infuse-Direct/8.5.9", string? range = null)
        {
            var c = new DefaultHttpContext(); c.Request.Method = method; c.Request.Path = route; c.Request.QueryString = new QueryString(query);
            c.Request.Headers.UserAgent = agent; if (range is not null) c.Request.Headers.Range = range;
            c.Response.Body = new MemoryStream(); await proxy.HandleAsync(c); return c;
        }
        var expected = VirtualMediaName.Chapters(@"\\nas\share\" + release);
        var listing = await Call("GET", "/emby/Items", "?Ids=12&Fields=Path,MediaSources");
        using (var json = JsonDocument.Parse(((MemoryStream)listing.Response.Body).ToArray()))
        {
            var item = json.RootElement.GetProperty("Items")[0]; var source = item.GetProperty("MediaSources")[0];
            check(item.GetProperty("Name").GetString() == "Original movie title", "virtual filenames do not rename the library movie title");
            check(source.GetProperty("Name").GetString() == expected.FileName && source.GetProperty("Path").GetString() == expected.Path && item.GetProperty("Path").GetString() == expected.Path,
                "chapter ISO item/source metadata use the same release-derived filename");
        }
        var head = await Call("HEAD", "/emby/Videos/12/stream.iso", "?Static=true&MediaSourceId=source");
        var part = await Call("GET", "/emby/Videos/12/stream.iso", "?Static=true&MediaSourceId=source", range: "bytes=0-63");
        check(head.Response.StatusCode == 200 && ContentDispositionHeaderValue.Parse(head.Response.Headers.ContentDisposition.ToString()).FileNameStar == expected.FileName,
            "chapter ISO HEAD has the same complete Unicode filename");
        check(part.Response.StatusCode == 206 && part.Response.Headers.ContentDisposition == head.Response.Headers.ContentDisposition &&
            part.Response.Headers.ETag == head.Response.Headers.ETag && part.Response.ContentLength == 64,
            "filename change preserves ISO Range, size and ETag behavior");
        using var input = image.Open(); var bytes = new byte[64]; input.ReadExactly(bytes);
        check(((MemoryStream)part.Response.Body).ToArray().SequenceEqual(bytes), "virtual filename change does not change ISO payload bytes");
        var ordinary = await Call("GET", "/emby/Items", "?Ids=12", "Emby-Test");
        check(((MemoryStream)ordinary.Response.Body).ToArray().SequenceEqual(fixture), "other players retain the original movie/source names and metadata");
    }

    private sealed class Catalog(byte[] json, long length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : json);
            if (request.Method == HttpMethod.Head) content.Headers.ContentLength = length;
            else content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
