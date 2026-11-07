using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.Proxy.DolbyVision;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text.Json;

internal static class IntegrationChecks
{
    internal static async Task Run(string[] args, Action<bool, string> check)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(args[1])); var url = saved.RootElement.GetProperty("embyUrl").GetString()!;
        var remux = args.Contains("--remux");
        var convert = !remux;
        if (!new Uri(url).IsLoopback) throw new Exception("Local test only.");
        var key = saved.RootElement.GetProperty("embyApiKey").GetString()!; var itemId = args[2];
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }); client.Timeout = TimeSpan.FromMinutes(2);
        using var originalRequest = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/emby/Items?Ids=" + Uri.EscapeDataString(itemId) + "&Fields=MediaSources,Chapters");
        originalRequest.Headers.Add("X-Emby-Token", key);
        using var original = await client.SendAsync(originalRequest); original.EnsureSuccessStatusCode();
        using var sourceDocument = JsonDocument.Parse(await original.Content.ReadAsStringAsync()); var source = sourceDocument.RootElement.GetProperty("Items")[0].GetProperty("MediaSources")[0];
        var config = new ProxyConfiguration { UpstreamBaseUrl = url, EnableInfuseDolbyVision = convert, InfuseDiscContainer = remux ? "mkv" : "iso", DolbyVisionFfmpegPath = args[3], DolbyVisionLibraryPath = args[4],
            EnableInfuseLanguagePatch = true, EnableInfuseChapters = true, EnableInfuseMultiClipPlayback = true };
        using var logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        var logger = logging.CreateLogger<EmbyProxy>(); using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
        using var runtime = new DvRuntime(DvOptions.From(config), logger); var proxy = new EmbyProxy(config, client, memory, logger, dvRuntime: runtime);
        var presentation = await runtime.Describe(source, CancellationToken.None, convert); var plan = presentation.Plan;
        Directory.CreateDirectory(args[5]);
        async Task<(DefaultHttpContext Context, byte[] Bytes)> Call(string method, string path, string query, string? range = null, string agent = "Infuse-Direct/8.5.9", string? credential = null, EmbyProxy? instance = null)
        {
            var c = new DefaultHttpContext(); c.Request.Method = method; c.Request.Path = path; c.Request.QueryString = new QueryString(query);
            c.Request.Headers.UserAgent = agent; c.Request.Headers["X-Emby-Token"] = credential ?? key;
            if (range is not null) c.Request.Headers.Range = range;
            c.Response.Body = new MemoryStream(); await (instance ?? proxy).HandleAsync(c);
            return (c, ((MemoryStream)c.Response.Body).ToArray());
        }
        var query = "?Static=true&MediaSourceId=" + Uri.EscapeDataString(source.GetProperty("Id").GetString()!) + (convert ? "&HelperDv=" : "&HelperRemux=") + plan.Signature;
        VirtualMediaName Name() => convert ? VirtualMediaName.DolbyVision(source.GetProperty("Path").GetString()!) : VirtualMediaName.Remux(source.GetProperty("Path").GetString()!);
        var route = "/emby/Videos/" + itemId + "/stream.mkv";
        var metadata = await Call("GET", "/emby/Items", "?Ids=" + itemId + "&Fields=MediaSources,Chapters");
        using (var parsed = JsonDocument.Parse(metadata.Bytes))
        {
            var item = parsed.RootElement.GetProperty("Items")[0]; var s = item.GetProperty("MediaSources")[0];
            check(s.GetProperty("Container").GetString() == "mkv" && s.GetProperty("Size").GetInt64() == plan.Length, "library metadata describes the virtual MKV");
            var named = Name();
            check(s.GetProperty("Path").GetString() == named.Path && s.GetProperty("Name").GetString() == named.FileName,
                "virtual MKV metadata retains the original release name and group");
            check(s.GetProperty("MediaStreams").EnumerateArray().Count(s => s.GetProperty("Type").GetString() == "Video") == (convert ? 1 : source.GetProperty("MediaStreams").EnumerateArray().Count(s => s.GetProperty("Type").GetString() == "Video")), "metadata preserves only the output video tracks");
        }
        var disabled = new EmbyProxy(config with { EnableInfuseDolbyVision = false, InfuseDiscContainer = "iso", EnableInfuseChapters = false }, client, memory, logger, dvRuntime: runtime);
        foreach (var (agent, instance) in new[] { ("Infuse-Direct/8.5.9", disabled), ("Emby-Test", proxy) })
        {
            var untouched = await Call("GET", "/emby/Items", "?Ids=" + itemId + "&Fields=MediaSources,Chapters", agent: agent, instance: instance);
            check(untouched.Bytes.SequenceEqual(await original.Content.ReadAsByteArrayAsync()), "disabled or non-Infuse metadata remains byte-identical: " + agent);
        }
        var head = await Call("HEAD", route, query);
        check(head.Context.Response.StatusCode == 200 && head.Context.Response.ContentLength == plan.Length && head.Bytes.Length == 0, "HEAD advertises exact seekable length without a media body");
        check(System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(head.Context.Response.Headers.ContentDisposition.ToString()).FileNameStar ==
            Name().FileName, "virtual MKV HTTP filename matches metadata, including Unicode");
        var headerA = await Call("GET", route, query, "bytes=0-1023"); var headerB = await Call("GET", route, query, "bytes=32-127");
        check(headerA.Context.Response.StatusCode == 206 && headerB.Bytes.SequenceEqual(headerA.Bytes[32..128]), "overlapping header ranges return identical bytes");
        foreach (var index in new[] { 0, 40, 41, plan.Segments.Count - 1 }.Concat(args[6..].Where(a => a != "--remux").Select(int.Parse)).Distinct())
        {
            var s = plan.Segments[index]; var offset = VirtualMatroska.HeaderBytes + s.SlotOffset;
            var a = await Call("GET", route, query, $"bytes={offset}-{offset + 63}"); var b = await Call("GET", route, query, $"bytes={offset + 8}-{offset + 31}");
            check(a.Context.Response.StatusCode == 206 && b.Bytes.SequenceEqual(a.Bytes[8..32]) && a.Bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1f, 0x43, 0xb6, 0x75 }), "random-access cluster and overlap " + index);
        }
        var denied = await Call("GET", route, query, "bytes=0-63", credential: "invalid-fixture-key");
        check(denied.Context.Response.StatusCode is 401 or 403, "cached media still requires fresh caller authorization");
        var blocked = await Call("GET", route, query, "bytes=0-63", agent: "Emby-Test");
        check(blocked.Context.Response.StatusCode == 403 && blocked.Bytes.Length == 0, "explicit DV URL does not serve other clients");
        var old = await Call("GET", route, query, "bytes=0-63", instance: disabled);
        check(old.Context.Response.StatusCode == 410 && old.Bytes.Length == 0, "disabled feature cannot return original disc bytes under a stale MKV URL");
        var invalid = await Call("GET", route, query, "bytes=0-4,8-12");
        check(invalid.Context.Response.StatusCode == 416 && invalid.Context.Response.Headers.ContentRange == $"bytes */{plan.Length}", "invalid multipart Range is rejected");
        var packets = new Dictionary<int, MatroskaFragment.Packet[]>();
        var builder = new DvFragmentBuilder(args[3], args[4], (input, output) =>
        {
            var bl = input.Tracks.Where(t => t.Type == 1).MaxBy(t => t.Width)!;
            if (remux) check(input.Packets.Count == output.Count && input.Packets.Zip(output).All(p => p.First == p.Second), "copy-only remux preserves all compressed packets, timestamps and durations");
            foreach (var track in input.Tracks.Where(t => t.Type != 1))
            {
                var before = input.Packets.Where(p => p.Track == track.Number).ToArray(); var after = output.Where(p => p.Track == track.Number).ToArray();
                check(before.Length == after.Length && before.Zip(after).All(p => p.First == p.Second), "audio/subtitle bytes and PTS unchanged, track " + track.Number);
            }
            static byte[] Pictures(IEnumerable<MatroskaFragment.Packet> list, int track)
            {
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                foreach (var p in list.Where(p => p.Track == track)) foreach (var nal in MatroskaFragment.Nals(p.Data, 4)) if ((nal.Span[0] >> 1 & 63) <= 31) hash.AppendData(nal.Span);
                return hash.GetHashAndReset();
            }
            check(Pictures(input.Packets, bl.Number).SequenceEqual(Pictures(output, bl.Number)), "actual disc compressed BL picture hash unchanged");
            // Production reuses scratch after the fragment is copied to its
            // cached cluster. This independent reference outlives that build.
            packets[packets.Count] = output.Select(p => p with { Data = p.Data.ToArray() }).ToArray();
        });
        var s0 = plan.Segments[40]; var s1 = plan.Segments[41];
        var f0 = await builder.Build(plan, s0, CancellationToken.None); var f1 = await builder.Build(plan, s1, CancellationToken.None);
        var combined = s0 with { End = s1.End, InputEnd = s1.InputEnd, SlotBytes = checked(s0.SlotBytes + s1.SlotBytes) };
        _ = await builder.Build(plan, combined, CancellationToken.None);
        foreach (var track in packets[2].Select(p => p.Track).Distinct())
        {
            var separate = packets[0].Concat(packets[1]).Where(p => p.Track == track).ToArray(); var together = packets[2].Where(p => p.Track == track).ToArray();
            check(separate.Length == together.Length && separate.Zip(together).All(p => p.First.TimeMs == p.Second.TimeMs && p.First.Data.Span.SequenceEqual(p.Second.Data.Span)), "adjacent on-demand segments match one continuous reference, track " + track);
        }
        File.WriteAllText(Path.Combine(args[5], "http-verification.json"), JsonSerializer.Serialize(new { schema = 1, date = DateTimeOffset.UtcNow, itemId, plan.Playlist, plan.Length,
            segments = plan.Segments.Count, headerBytes = VirtualMatroska.HeaderBytes, cache = runtime.Cache.Status(), result = "passed" }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
