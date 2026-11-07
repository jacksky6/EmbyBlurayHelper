using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.App;

var count = 0;
var directory = Path.Combine(Path.GetTempPath(), "helper-feature-batch-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var original = Path.Combine(directory, "original-probe"); var modern = Path.Combine(directory, "modern-probe");
    File.WriteAllText(original, "original"); File.WriteAllText(modern, "modern");
    var settings = new SettingsStore(directory);
    settings.Save(settings.Current with { EmbyApiKey = "private-feature-test-key", EnableMediaFeatures = true, UpstreamProbePath = original, FeatureProbePath = modern });
    var backend = new FeatureBackend(); using var client = new HttpClient(backend); var log = new HelperLog(directory);
    Check(MediaFeatureCatalog.Analyze(Element(FeatureBackend.Movie("mkv", "/films/BDMV/movie.mkv", "bluray"))) is null, "MKV cannot enter the plan through an incorrect bluray container");
    Check(MediaFeatureCatalog.Analyze(Element(FeatureBackend.Movie("clip", "/films/BDMV/STREAM/00001.m2ts", "bluray"))) is null, "loose M2TS inside BDMV is excluded");
    Check(MediaFeatureCatalog.Analyze(Element(FeatureBackend.Movie("mp4", "/films/movie.mp4", "mp4"))) is null, "ordinary MP4 is excluded");
    Check(MediaFeatureCatalog.Analyze(Element(FeatureBackend.Movie("iso-path", "/films/movie.ISO", "")))?.Sources.Single().Kind == "ISO", "ISO extension is recognized even before Emby has a container");
    Check(MediaFeatureCatalog.Analyze(Element(FeatureBackend.Movie("index", "/films/movie/BDMV/index.bdmv", "")))?.Sources.Single().Kind == "BDMV", "BDMV index is recognized");
    backend.Items["bd"] = FeatureBackend.Movie("bd", "/films/bd", "bluray");
    backend.Items["iso"] = FeatureBackend.Movie("iso", "/films/movie.iso", "iso");
    backend.Items["shared"] = FeatureBackend.Movie("shared", "/films/shared", "bluray");
    var multi = FeatureBackend.Movie("multi", "/films/movie.mkv", "mkv");
    multi["MediaSources"]!.AsArray().Add(FeatureBackend.Source("disc-multi-1", "/films/multi", "bluray"));
    multi["MediaSources"]!.AsArray().Add(FeatureBackend.Source("disc-multi-2", "/films/multi.iso", "iso"));
    backend.Items["multi"] = multi;
    var ordinary = Enumerable.Range(0, 202).Select(i => "normal-" + i).ToArray();
    foreach (var id in ordinary) backend.Items[id] = FeatureBackend.Movie(id, "/films/" + id + ".mkv", "mkv");
    backend.Groups["a"] = ordinary.Concat(new[] { "bd", "shared" }).ToList();
    backend.Groups["b"] = ["iso", "shared", "multi"]; backend.Groups["empty"] = [];
    await using (var task = new MediaFeatureBatch(settings, client, log, backend.Probe))
    {
        task.Scan(); var ready = await Idle(task);
        Check(N(ready, "moviesChecked") == 206 && N(ready.GetProperty("summary"), "discs") == 4, "pagination scans all movies but lists only disc movies");
        Check(backend.Offsets.Contains(("a", 200)), "catalog reads later pages");
        Check(ready.GetProperty("libraries").GetArrayLength() == 3 && N(ready.GetProperty("libraries")[1], "discs") == 3, "shared movie belongs to both libraries and empty libraries remain selectable");
        Throws(() => task.Fill(S(ready, "id"), []), "empty scope cannot process all libraries");
        Throws(() => task.Fill(S(ready, "id"), ["unknown"]), "unknown library is rejected");
        task.Fill(S(ready, "id"), ["a"]); var done = await Idle(task);
        Check(N(done, "completed") == 2 && backend.Writes.Select(w => w.Item1).Order().SequenceEqual(new[] { "bd", "shared" }), "selected library cannot probe unselected exclusive movies");
        Check(N(done.GetProperty("summary"), "verified") == 2, "normal samples without additional Atmos or DV count as verified");
        task.Fill(S(done, "id"), ["b"]); done = await Idle(task);
        Check(N(done, "completed") == 2 && backend.Writes.Count(w => w.Item1 == "shared") == 1, "shared movie is skipped after successful verification in another library");
        Check(backend.Writes.Where(w => w.Item1 == "multi").Select(w => w.Item2).SequenceEqual(new[] { "disc-multi-1", "disc-multi-2" }), "mixed-version movie probes only its explicit disc source IDs");
        Check(N(done.GetProperty("summary"), "pending") == 0, "all disc versions must finish before movie is verified");
        task.Scan(); ready = await Idle(task);
        Check(N(ready.GetProperty("summary"), "verified") == 4, "rescan retains valid positive and negative verification history");
        Throws(() => task.Fill(S(ready, "id"), ["a"]), "completed libraries do not silently repeat probes");
        var writes = backend.Writes.Count;
        task.Fill(S(ready, "id"), ["a"], true); done = await Idle(task);
        Check(backend.Writes.Count == writes + 2, "explicit recheck includes completed movies");
        Check(!File.ReadAllText(Path.Combine(directory, "media-feature-task.json")).Contains("private-feature-test-key"), "persisted task contains no API credentials");

        backend.Mode = "no-receipt";
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(N(done, "failed") == 2 && N(done, "completed") == 0, "missing proof of supplementary success cannot be counted as completed");
        Check(N(done.GetProperty("summary"), "pending") == 2, "a failed forced recheck clears its previous success so the default resume can retry it");
        backend.Mode = "unsaved";
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(N(done, "failed") == 2 && N(done, "completed") == 0, "unsaved Atmos response fails read-back verification");
        backend.Mode = "normal";
        backend.Groups["a"].Remove("bd"); writes = backend.Writes.Count;
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(N(done, "skipped") == 1 && backend.Writes.Count == writes + 1, "fresh library membership excludes moved-out movies");
        backend.Groups["a"].Add("bd");
        backend.Items["bd"]["MediaSources"]![0]!["Path"] = "/films/replaced";
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(N(done, "failed") == 1, "changed source path cannot receive an old plan's write");
        task.Scan(); ready = await Idle(task);
        Check(N(ready.GetProperty("summary"), "pending") == 1, "only changed source loses verification history on rescan");
        backend.Mode = "hold"; backend.Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        task.Fill(S(ready, "id"), ["a"]);
        await backend.Reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Throws(() => task.Scan(), "concurrent scan cannot replace an active task");
        task.Cancel(); done = await Idle(task);
        Check(S(done, "status") == "cancelled" && N(done, "completed") == 0, "cancelled pending probe is not claimed as completed");
        backend.Mode = "normal";
        task.Fill(S(done, "id"), ["a"]); done = await Idle(task);
        Check(N(done, "completed") == 1, "cancelled task can resume pending sources");

        settings.Save(settings.Current with { EmbyApiKey = "rotated" });
        Throws(() => task.Fill(S(done, "id"), ["a"], true), "connection change invalidates stale plans");
        settings.Save(settings.Current with { EmbyApiKey = "private-feature-test-key" });
        backend.Server = "new-server"; writes = backend.Writes.Count;
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(S(done, "status") == "failed" && backend.Writes.Count == writes, "server identity must match before writes");
        backend.Server = "feature-server"; backend.Capable = false;
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(S(done, "status") == "failed" && S(done, "message").Contains("0.1.4"), "old plugin produces the required upgrade explanation");
        backend.Capable = true; backend.Connected = false;
        task.Fill(S(done, "id"), ["a"], true); done = await Idle(task);
        Check(S(done, "status") == "failed" && backend.Writes.Count == writes, "disconnected wrapper cannot start feature probing");
        backend.Connected = true;
        settings.Save(settings.Current with { EnableMediaFeatures = false });
        Throws(() => task.Fill(S(done, "id"), ["a"], true), "feature switch is required independently of language setting");
        settings.Save(settings.Current with { EnableMediaFeatures = true });
    }
    await using (var restored = new MediaFeatureBatch(settings, client, log, backend.Probe))
    {
        var saved = Snapshot(restored);
        Check(saved.GetProperty("scanComplete").GetBoolean() && N(saved.GetProperty("summary"), "verified") == 4, "progress and verified sources survive process restart");
        File.AppendAllText(modern, "new engine");
        Check(!Snapshot(restored).GetProperty("connectionMatches").GetBoolean(), "engine replacement invalidates old plans");
        restored.Scan(); var ready = await Idle(restored);
        Check(N(ready.GetProperty("summary"), "pending") == 4, "new engine can recheck previous negative samples");
    }
    Check(log.Entries().Any(e => e.Source == "MediaFeatureBatch" && e.Message.Contains("媒体源")), "logs retain per-source verification results");
    await using (var scoped = new MediaFeatureBatch(settings, client, log, backend.Probe))
    {
        var previous = Snapshot(scoped);
        scoped.Fill(S(previous,"id"), null, true); previous = await Idle(scoped);
        settings.Save(settings.Current with { ExcludedLibrariesByServer = new() { ["feature-server"] = ["b"] } });
        Check(Snapshot(scoped).GetProperty("libraries").GetArrayLength() == 0 && N(Snapshot(scoped).GetProperty("summary"), "discs") == 0, "specs discard stale scope from visible libraries and totals");
        Throws(() => scoped.Fill(S(previous,"id"), ["b"], true), "specs reject old plans after global exclusion");
        scoped.Scan(); var filtered = await Idle(scoped);
        Check(!filtered.GetProperty("libraries").EnumerateArray().Any(l => S(l,"id") == "b"), "spec checks exclude disallowed libraries");
        Throws(() => scoped.Fill(S(filtered,"id"), ["b"], true), "spec fill rejects excluded library IDs");
        settings.Save(settings.Current with { ExcludedLibrariesByServer = new() });
        scoped.Scan(); var restored = await Idle(scoped);
        Check(N(restored.GetProperty("summary"), "verified") == 4, "restored libraries retain positive and negative specification receipts without probing again");
    }
    Console.WriteLine($"Passed {count} media-feature scope, batch and persistence checks.");
}
finally { Directory.Delete(directory, true); }

void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
void Throws(Action action, string message) { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { count++; return; } throw new Exception(message); }
static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
static JsonElement Snapshot(MediaFeatureBatch task) => JsonSerializer.SerializeToElement(task.Snapshot(), SettingsStore.Json);
static string S(JsonElement value, string key) => value.GetProperty(key).GetString() ?? "";
static int N(JsonElement value, string key) => value.GetProperty(key).GetInt32();
static async Task<JsonElement> Idle(MediaFeatureBatch task)
{
    for (var n = 0; n < 500; n++) { var state = Snapshot(task); if (!state.GetProperty("active").GetBoolean()) return state; await Task.Delay(10); }
    throw new Exception("Batch failed to become idle: " + Snapshot(task));
}

sealed class FeatureBackend : HttpMessageHandler
{
    internal Dictionary<string, JsonObject> Items = [];
    internal Dictionary<string, List<string>> Groups = [];
    internal List<(string, string)> Writes = [];
    internal List<(string, int)> Offsets = [];
    internal string Mode = "normal", Server = "feature-server";
    internal bool Capable = true, Connected = true;
    internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async Task<JsonElement> Probe(HelperSettings config, string itemId, string sourceId, CancellationToken token)
    {
        Writes.Add((itemId, sourceId));
        if (Mode == "hold") { Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        var source = Items[itemId]["MediaSources"]!.AsArray().Single(s => s!["Id"]!.ToString() == sourceId)!;
        var streams = source["MediaStreams"]!.DeepClone();
        if (Mode == "unsaved") streams[1]!["Profile"] = "Dolby TrueHD + Dolby Atmos";
        return JsonSerializer.SerializeToElement(new { verified = Mode != "no-receipt", mediaSourceId = sourceId, streams });
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        object response;
        if (path.EndsWith("System/Info/Public")) response = new { Id = Server };
        else if (path.EndsWith("Library/VirtualFolders")) response = Groups.Keys.Select(id => new { ItemId = id, Name = "Library " + id, CollectionType = "" })
            .Append(new { ItemId = "collections", Name = "合集", CollectionType = "boxsets" }).ToArray();
        else if (path.EndsWith("BlurayHelper/Status")) response = new { capabilities = Capable ? new[] { "media-features-verification" } : [] };
        else if (path.EndsWith("Encoding/FfmpegOptions")) response = new { Object = new { UseCustomProbePath = Connected, CustomProbePath = Environment.ProcessPath } };
        else if (path.EndsWith("Items"))
        {
            var ids = query["Ids"];
            var library = query["ParentId"];
            var offset = int.Parse(query["StartIndex"] ?? "0");
            var items = ids is not null ? Items.Values.Where(i => i["Id"]!.ToString() == ids).ToArray()
                : Groups[library!].Select(id => Items[id]).ToArray();
            if (ids is null) Offsets.Add((library!, offset));
            response = new { Items = items.Skip(offset).Take(int.Parse(query["Limit"] ?? "200")).ToArray(), TotalRecordCount = items.Length };
        }
        else throw new Exception("Unexpected route " + request.RequestUri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), System.Text.Encoding.UTF8, "application/json") });
    }
    internal static JsonObject Movie(string id, string path, string container) => new()
    { ["Id"] = id, ["Name"] = "Movie " + id, ["MediaSources"] = new JsonArray(Source("source-" + id, path, container)) };
    internal static JsonObject Source(string id, string path, string container) => new()
    {
        ["Id"] = id, ["Path"] = path, ["Container"] = container, ["RunTimeTicks"] = 60_000_000_000L, ["Size"] = 50_000_000_000L,
        ["MediaStreams"] = JsonNode.Parse("""[{"Index":0,"Type":"Video","Codec":"hevc","Width":3840,"Height":2160,"ExtendedVideoType":"Hdr10","ExtendedVideoSubType":"Hdr10"},{"Index":1,"Type":"Audio","Codec":"truehd","Channels":8,"Language":"zho","Title":"我的备注"},{"Index":2,"Type":"Subtitle","Codec":"pgssub","Language":"zho","IsExternal":false}]""")
    };
}
