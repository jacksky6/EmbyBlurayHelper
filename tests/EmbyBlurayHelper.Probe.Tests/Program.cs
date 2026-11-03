using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using EmbyBlurayHelper.ProbeWrapper;

if (args.FirstOrDefault() == "--features-disc") return await MediaFeatureChecks.ReadDisc(args[1..]);
if (args.FirstOrDefault() == "--make-feature-fixture") return MediaFeatureChecks.MakeFixture(args[1], args[2]);
await MediaFeatureChecks.Run();
if (args.FirstOrDefault() == "--inspect-disc") return DiscInspection.Run(args[1..]);
if (args.FirstOrDefault() == "--probe-bdmv")
{
    foreach (var path in args[2..])
    {
        var indexPath = Path.Combine(path, "BDMV", "index.bdmv");
        var result = await IsoMediaProbe.RunDirectory(args[1], ["-i", indexPath, "-v", "error", "-of", "json", "-show_streams", "-show_format", "-show_chapters"], indexPath, default);
        using var json = JsonDocument.Parse(result.Output);
        if (!json.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "audio") ||
            double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) < 3600)
            throw new InvalidOperationException("BDMV has no audio or feature duration.");
        Console.WriteLine(Encoding.UTF8.GetString(result.Output));
    }
    return 0;
}

var checks = 0;
var upstream = args.FirstOrDefault(arg => arg.StartsWith("--upstream="))?[11..];
var wrapper = args.FirstOrDefault(arg => arg.StartsWith("--wrapper="))?[10..];
Check(IsoMediaProbe.RequestedPlaylist(["-playlist", "2"]) == "00002.MPLS", "explicit playlist selection is normalized");
Check(IsoDiscMetadata.IsRepetitive(Enumerable.Repeat(("menu", 600d, 624d), 280)), "repeated menu intervals are rejected");
Check(IsoDiscMetadata.IsRepetitive(new[] { ("logo", 0d, 84d) }.Concat(Enumerable.Repeat(("menu", 11d, 180d), 201))),
    "a logo cannot disguise the repeated menu found in The Imitation Game");
Check(!IsoDiscMetadata.IsRepetitive([("main", 0d, 1000d), ("main", 1000d, 2000d)]),
    "distinct portions of the same M2TS remain eligible");
Check(!IsoDiscMetadata.IsRepetitive([("logo", 0d, 1d), ("main", 0d, 6000d), ("logo", 0d, 1d)]),
    "a repeated boundary logo does not exclude a feature");
try { IsoMediaProbe.RequestedPlaylist(["-playlist", "../2"]); throw new InvalidOperationException("unsafe playlist accepted"); }
catch (ArgumentException) { checks++; }
var selectedPlaylist = args.FirstOrDefault(arg => arg.StartsWith("--playlist="))?[11..];
foreach (var isoPath in args.Where(arg => !arg.StartsWith("--")))
{
    var languageByPid = BluRayLanguageAugmenter.ReadIsoLanguageMap(isoPath, selectedPlaylist);
    Check(languageByPid.Count > 0, $"{Path.GetFileName(isoPath)} contains audio/subtitle language metadata");

    var streams = new JsonArray();
    var index = 0;
    foreach (var pair in languageByPid.OrderBy(pair => pair.Key))
    {
        streams.Add(new JsonObject
        {
            ["index"] = index++,
            ["id"] = "0x" + pair.Key.ToString("X4"),
            ["codec_type"] = pair.Value.Kind,
            ["tags"] = new JsonObject { ["language"] = "und" }
        });
    }

    var preserved = streams[0]!["tags"]!["language"]!.GetValue<string>();
    var firstLanguage = languageByPid.OrderBy(pair => pair.Key).First().Value.Language;
    preserved = firstLanguage.Equals("eng", StringComparison.OrdinalIgnoreCase) ? "jpn" : "eng";
    streams[0]!["tags"]!["language"] = preserved;
    var fixture = new JsonObject { ["streams"] = streams, ["format"] = new JsonObject { ["filename"] = "bluray:" + isoPath } };
    var probeArguments = new[] { "-i", $"file:\"{isoPath}\"", "-print_format", "json" };

    Check(BluRayLanguageAugmenter.ShouldCapture(probeArguments), $"{Path.GetFileName(isoPath)} JSON probe is captured");
    Check(BluRayLanguageAugmenter.GetIsoInput(probeArguments) == isoPath, $"{Path.GetFileName(isoPath)} is passed to the unified ISO reader");
    var patchedBytes = BluRayLanguageAugmenter.TryPatch(
        Encoding.UTF8.GetBytes(fixture.ToJsonString()), probeArguments, out var diagnostic, selectedPlaylist);
    using var patched = JsonDocument.Parse(patchedBytes);
    Check(patched.RootElement.GetProperty("format").GetProperty("filename").GetString() == isoPath,
        $"{Path.GetFileName(isoPath)} returns the original ISO filename to Emby");
    var outputStreams = patched.RootElement.GetProperty("streams");
    foreach (var pair in languageByPid.OrderBy(pair => pair.Key))
    {
        var stream = outputStreams.EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == "0x" + pair.Key.ToString("X4"));
        var expected = pair.Key == languageByPid.Keys.Min() ? preserved : pair.Value.Language;
        Check(stream.GetProperty("tags").GetProperty("language").GetString() == expected,
            $"{Path.GetFileName(isoPath)} PID 0x{pair.Key:X4} maps to {expected} without overwriting known tags");
    }

    Check(!string.IsNullOrWhiteSpace(diagnostic) && diagnostic.Contains("audio:") && diagnostic.Contains("subtitles:"),
        $"{Path.GetFileName(isoPath)} reports separate audio/subtitle diagnostics");
    Console.WriteLine($"{Path.GetFileName(isoPath)}: {languageByPid.Count} PID/language mappings verified.");
    if (upstream is not null && wrapper is not null)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(wrapper)
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true } };
        process.StartInfo.Environment["EMBY_BLURAY_HELPER_UPSTREAM_PROBE"] = upstream;
        foreach (var arg in new[] { "-protocol_blacklist", "bluray", "-i", isoPath, "-v", "error", "-of", "json", "-show_streams", "-show_format", "-show_chapters" })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        var log = await error;
        Check(process.ExitCode == 0 && log.Contains("UDF/M2TS reader succeeded") && !log.Contains("Protocol not found") && !log.Contains("[bluray @"),
            $"the unified reader works without invoking the native Blu-ray protocol: {log}");
        var actual = JsonNode.Parse(await output)!;
        var actualStreams = actual["streams"]!.AsArray().OfType<JsonObject>().ToArray();
        Check(actual["format"]!["filename"]!.GetValue<string>() == isoPath &&
              long.Parse(actual["format"]!["size"]!.ToString()) == new FileInfo(isoPath).Length,
            "reader preserves ISO source identity and full file size");
        Check(double.Parse(actual["format"]!["duration"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture) > 3600,
            "feature duration comes from MPLS, not a short probe sample");
        Check(actualStreams.Any(s => s["codec_type"]!.GetValue<string>() == "video" && s["width"]!.GetValue<int>() >= 1920), "real HD video parameters are returned");
        Check(actualStreams.Where(s => s["codec_type"]!.GetValue<string>() is "audio" or "subtitle")
            .All(s => !string.IsNullOrWhiteSpace(s["tags"]?["language"]?.ToString())), "all sampled audio and subtitle PIDs receive playlist languages");
        Check(actualStreams.Select(s => s["index"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(0, actualStreams.Length)) &&
            actual["format"]!["nb_streams"]!.GetValue<int>() == actualStreams.Length, "stream indices and count remain consistent");
        Check(actual["chapters"] is JsonArray { Count: > 0 }, "playlist chapters survive the unified reader");
        if (Path.GetFileName(isoPath).StartsWith("Shutter.Island.2010.UHD", StringComparison.OrdinalIgnoreCase))
        {
            Check(actualStreams.Count(s => s["codec_type"]?.ToString() == "video") == 2 &&
                  actualStreams.Any(s => s["codec_name"]?.ToString() == "truehd") &&
                  actualStreams.Any(s => s["codec_name"]?.ToString() == "ac3"),
                "UHD fixture retains its Dolby Vision enhancement video and TrueHD/AC3 pair");
        }
        Console.WriteLine($"ISO reader integration: {Path.GetFileName(isoPath)}, {actualStreams.Length} streams, {actual["format"]!["duration"]}s.");
    }
}

var ordinaryVideoArguments = new[] { "-i", "D:\\media\\movie.mkv", "-print_format", "json" };
Check(!BluRayLanguageAugmenter.ShouldCapture(ordinaryVideoArguments), "ordinary file probes are passed through");
Check(!BluRayLanguageAugmenter.ShouldCapture(["-i", "D:\\media\\movie.iso", "-v", "info"]),
    "non-JSON ISO probes are not captured");
Check(IsoMediaHttpSource.TryRange("bytes=100-199", 1000, out var rangeStart, out var rangeEnd) && rangeStart == 100 && rangeEnd == 199,
    "ISO HTTP source handles ordinary byte ranges");
Check(IsoMediaHttpSource.TryRange("bytes=-100", 1000, out rangeStart, out rangeEnd) && rangeStart == 900 && rangeEnd == 999,
    "ISO HTTP source handles suffix byte ranges");
Check(!IsoMediaHttpSource.TryRange("bytes=1000-", 1000, out _, out _) &&
      !IsoMediaHttpSource.TryRange("bytes=10-1", 1000, out _, out _) &&
      !IsoMediaHttpSource.TryRange("bytes=0-1,3-4", 1000, out _, out _), "invalid and multipart ranges are rejected");
using (var sample = new MemoryStream(Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray()))
await using (var source = new IsoMediaHttpSource(sample, CancellationToken.None))
using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
{
    async Task VerifyRange(int start, int end)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Check(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length == 8192 &&
            bytes.SequenceEqual(Enumerable.Range(start, end - start + 1).Select(i => (byte)(i % 251))), "HTTP range returns exact ISO bytes and seekable size");
    }
    await Task.WhenAll(VerifyRange(0, 1023), VerifyRange(4000, 4999), VerifyRange(7000, 8191));
    using var bad = await http.GetAsync(new Uri(new Uri(source.Url), "/wrong"));
    Check(bad.StatusCode == HttpStatusCode.NotFound, "the temporary source exposes only its random resource");
    using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, source.Url));
    Check(head.IsSuccessStatusCode && head.Content.Headers.ContentLength == 8192, "HEAD returns member size without reading media");
}

Console.WriteLine($"Passed {checks} ISO probe checks.");
return 0;

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
