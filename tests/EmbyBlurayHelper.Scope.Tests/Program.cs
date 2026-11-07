using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmbyBlurayHelper;
using EmbyBlurayHelper.App;

if (args.FirstOrDefault() == "--scope-probe-fixture") { Console.Write(JsonSerializer.Serialize(args)); return; }
var root = Path.Combine(Path.GetTempPath(), "bluray-scope-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var previousExport = Environment.GetEnvironmentVariable("EMBY_BLURAY_HELPER_PROBE_CONFIG");
Environment.SetEnvironmentVariable("EMBY_BLURAY_HELPER_PROBE_CONFIG", Path.Combine(root, "probe-settings.json"));
var count = 0;
try
{
    var store = new SettingsStore(root);
    store.Save(store.Current with { EmbyApiKey = "scope-private-key", UpstreamProbePath = Environment.ProcessPath! });
    using var log = new HelperLog(root);
    using var backend = new ScopeBackend();
    using var http = new HttpClient(backend);
    var scope = new LibraryScopeService(store, http, log);
    var view = await scope.Read(default);
    Check(view.AllowedCount == 3 && view.ExcludedCount == 0, "first use allows every library, regardless of type");
    Check(view.Libraries.All(l => l.Id != "collections") && view.Libraries.Any(l => l.Name == "合集" && l.Id == "movies"), "system collections are hidden by type while a real movie library named Collections is retained");
    Check(!SettingsStore.LibraryTypeAllowed(" BoXsEtS ") && SettingsStore.LibraryTypeAllowed(null) && SettingsStore.LibraryTypeAllowed("movies"), "only the system collection type is filtered; unset and ordinary library types remain eligible");
    store.Save(store.Current with { ExcludedLibrariesByServer = new() { ["server-one"] = ["collections"] } });
    view = await scope.Read(default);
    Check(view.AllowedCount == 3 && view.ExcludedCount == 0, "a previously saved collection exclusion does not affect visible library counts");
    view = await scope.Save(new("server-one", ["tv"]), default);
    Check(view.AllowedCount == 2 && view.Synchronized && backend.Excluded.SequenceEqual(["tv"]), "policy is confirmed by Emby before it is committed");
    Check(new SettingsStore(root).Current.ExcludedLibrariesByServer["server-one"].SequenceEqual(["tv"]), "exclusions survive a Helper restart or upgrade");
    var exported = File.ReadAllText(Path.Combine(root, "probe-settings.json"));
    Check(!exported.Contains("scope-private-key") && exported.Contains("/media/tv"), "probe receives excluded roots without API credentials");
    Check(ProbeSettings.From(store.Current).ExcludedLibraryPaths!.SequenceEqual(["/media/tv"]), "standalone Windows probe sees the same scope as shared DSM probe");
    backend.TvName = "Renamed TV";
    view = await scope.Read(default);
    Check(!view.Libraries.Single(l => l.Id == "tv").Allowed, "rename preserves the ID-based exclusion");
    backend.NewLibrary = true;
    view = await scope.Read(default);
    Check(view.Libraries.Single(l => l.Id == "new").Allowed, "new library is allowed automatically");
    var catalog = new MediaFeatureCatalog(store, http);
    Check(!(await catalog.Libraries(store.Current, default)).Any(l => l.Id == "tv"), "shared batch catalog excludes disallowed libraries");
    Check(!(await catalog.Libraries(store.Current, default)).Any(l => l.Id == "collections"), "system collections do not enter feature or chapter batch catalogs");
    await Reject(async () => await catalog.Movies(store.Current, "collections", _ => { }, default), "direct enumeration cannot scan a system collection");
    await Reject(async () => await scope.Save(new("server-one", ["collections"]), default), "stale forms cannot reintroduce a system collection as a configurable library");
    await Reject(async () => await catalog.Movies(store.Current, "tv", _ => { }, default), "direct enumeration cannot bypass the policy");
    var before = SettingsStore.ScopeStamp(store.Current);
    await Reject(async () => await scope.Save(new("other-server", ["movies"]), default), "stale server ID is rejected");
    await Reject(async () => await scope.Save(new("server-one", ["unknown"]), default), "unknown library IDs are rejected");
    Check(SettingsStore.ScopeStamp(store.Current) == before, "invalid requests do not change policy");
    backend.IgnoreMutation = true;
    await Reject(async () => await scope.Save(new("server-one", ["movies"]), default), "ignored Emby configuration is not reported as saved");
    Check(SettingsStore.ScopeStamp(store.Current) == before && backend.Excluded.SequenceEqual(["tv"]), "failed confirmation retains previous policy");
    backend.IgnoreMutation = false;
    backend.Installed = false;
    view = await scope.Read(default);
    Check(!view.PluginReady && view.Message.Contains("0.1.9"), "older DLL gets an explicit upgrade and restart explanation");
    await Reject(async () => await scope.Save(new("server-one", []), default), "scope cannot be saved without automatic-provider support");
    backend.Installed = true;
    store.Save(store.Current with { EmbyUrl = "http://second-server:8096" });
    backend.Server = "server-two"; backend.Excluded = [];
    Check(ProbeSettings.From(store.Current).ExcludedLibraryPaths!.Length == 0, "changing connection never reuses another server's roots");
    view = await scope.Read(default);
    Check(view.ExcludedCount == 0, "same library ID on another server is allowed");
    await scope.Save(new("server-two", ["movies"]), default);
    Check(store.Current.ExcludedLibrariesByServer["server-one"].SequenceEqual(["tv"]) && store.Current.ExcludedLibrariesByServer["server-two"].SequenceEqual(["movies"]), "server policies are stored independently");
    store.Save(store.Current with { EmbyUrl = "http://127.0.0.1:8096" }); backend.Server = "server-one"; backend.Excluded = [];
    await scope.Prepare(default);
    Check(backend.Excluded.SequenceEqual(["tv"]) && ProbeSettings.From(store.Current).ExcludedLibraryPaths!.SequenceEqual(["/media/tv"]), "switching back restores the original server policy and probe projection");
    Check(LibraryPathScope.Contains("/media/tv", "/media/tv/a.iso") && !LibraryPathScope.Contains("/media/tv", "/media/tv2/a.iso"), "Unix root boundaries are respected");
    Check(!LibraryPathScope.Contains("/media/tv", "/media/TV/a.iso"), "Unix case remains significant");
    Check(LibraryPathScope.Contains(@"D:\Movies", @"d:\movies\A\BDMV\index.bdmv"), "Windows paths ignore case");
    Check(LibraryPathScope.Contains(@"\\nas\web", @"\\NAS\WEB\a.iso"), "UNC paths ignore case");
    Check(LibraryPathScope.Contains("/media/tv", "bluray:/media/tv/a") && LibraryPathScope.Contains("/media/tv", "file:///media/tv/a.iso"), "disc and file URI prefixes are normalized");
    Check(LibraryPathScope.Allowed(["/media/tv"], "/media/movies/a.iso") && LibraryPathScope.Allowed(["/media/tv"], null), "unrelated files and version commands remain allowed");
    Check(!LibraryPathScope.Allowed(["/media/tv"], "concat:/media/movies/a.m2ts|/media/tv/b.m2ts"), "any excluded segment prevents concat augmentation");
    Check(LibraryPathScope.Contains("/media/tv", "///media/tv/a.iso") && LibraryPathScope.Contains(@"D:\Movies", "///D:/Movies/a.iso"), "already-normalized file URI paths retain scope on Unix and Windows");
    if (args.Length == 1)
    {
        var helper = Path.GetFullPath(args[0]);
        foreach (var input in new[] { "/media/tv/not-a-real-disc.iso", "concat:/media/movies/a.m2ts|/media/tv/b.m2ts", "file:///media/tv/not-a-real-disc.iso" })
        {
        var invocation = new[] { "--scope-probe-fixture", "-print_format", "json", "-show_streams", "-i", input };
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["EMBY_BLURAY_HELPER_DATA"] = root;
        start.ArgumentList.Add("--probe"); foreach (var argument in invocation) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0 && JsonSerializer.Deserialize<string[]>(await stdout)!.SequenceEqual(invocation), "excluded ISO is passed unchanged to original ffprobe without opening it");
        Check(!(await stderr).Contains("disc reader failed"), "excluded disc never invokes supplementary disc reading");
        }
    }
    Check(backend.Authenticated && backend.Buffered, "scope API requests use token headers and fixed content length");
    Console.WriteLine($"Passed {count} global processing scope checks.");
}
finally { Environment.SetEnvironmentVariable("EMBY_BLURAY_HELPER_PROBE_CONFIG", previousExport); Directory.Delete(root, true); }
void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
async Task Reject(Func<Task> action, string message) { try { await action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { count++; return; } throw new Exception(message); }

sealed class ScopeBackend : HttpMessageHandler
{
    internal string Server = "server-one", TvName = "TV";
    internal string[] Excluded = [];
    internal bool NewLibrary, IgnoreMutation, Installed = true, Authenticated = true, Buffered = true;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Authenticated &= request.Headers.TryGetValues("X-Emby-Token", out var values) && values.Single() == "scope-private-key";
        var path = request.RequestUri!.AbsolutePath;
        object response;
        if (path.EndsWith("Info/Public")) response = new { Id = Server };
        else if (path.EndsWith("VirtualFolders"))
        {
            var folders = new List<object> { new { ItemId = "movies", Name = "合集", CollectionType = "movies", Locations = new[] { "/media/movies" } },
                new { ItemId = "tv", Name = TvName, CollectionType = "tvshows", Locations = new[] { "/media/tv" } },
                new { ItemId = "music", Name = "Music", CollectionType = "music", Locations = new[] { "/media/music" } },
                new { ItemId = "collections", Name = "Renamed Collections", CollectionType = "BoXsEtS", Locations = Array.Empty<string>() } };
            if (NewLibrary) folders.Add(new { ItemId = "new", Name = "New", CollectionType = "tvshows", Locations = new[] { "/media/new" } });
            response = folders;
        }
        else if (path.EndsWith("LibraryScope"))
        {
            if (!Installed) return new(HttpStatusCode.NotFound);
            if (request.Content is not null)
            {
                Buffered &= request.Content.Headers.ContentLength > 0;
                var body = await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                if (!IgnoreMutation) Excluded = body.GetProperty("ExcludedLibraryIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
            }
            response = new { serverId = Server, excludedLibraryIds = Excluded };
        }
        else throw new Exception("Unexpected route " + path);
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: new JsonSerializerOptions()) };
    }
}
