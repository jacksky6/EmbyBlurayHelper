using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.App;

var count = 0;
var root = Path.Combine(Path.GetTempPath(), "native-chapter-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new SettingsStore(root); store.Save(store.Current with { EmbyApiKey = "private-chapter-key" });
    var backend = new Backend(); using var http = new HttpClient(backend); var log = new HelperLog(root);
    var service = new NativeChapterService(store, http, log);
    var status = Element(await service.Status(default));
    Check(status.GetProperty("ready").GetBoolean() && !status.GetProperty("enabled").GetBoolean(), "status shows independent disabled switch");
    await service.Configure(true, "/helper/EmbyBlurayHelper", default);
    Check(store.Current.EnableNativeChapters && backend.Enabled && !store.Current.EnableMediaFeatures, "chapter switch needs no spec probe and saves after plugin acknowledgement");
    backend.IgnoreConfiguration = true;
    await ThrowsAsync(() => service.Configure(false, "/helper/EmbyBlurayHelper", default), "unacknowledged switch does not persist");
    Check(store.Current.EnableNativeChapters, "failed switch retains saved value"); backend.IgnoreConfiguration = false;
    backend.Installed = false; status = Element(await service.Status(default));
    Check(!status.GetProperty("ready").GetBoolean() && Text(status, "error").Contains("0.1.5"), "missing plugin gives DLL and restart instruction"); backend.Installed = true;
    backend.Add("bd", "/films/bd", "bluray", "generated", "a");
    backend.Add("iso", "/films/film.iso", "iso", "empty", "b");
    backend.Add("shared", "/films/shared", "bluray", "empty", "a", "b");
    backend.Add("custom", "/films/custom", "bluray", "existing", "a");
    backend.Add("normal", "/films/normal.mkv", "mkv", "empty", "a");
    backend.Add("clip", "/films/BDMV/STREAM/00001.m2ts", "bluray", "empty", "a");
    await using (var batch = new NativeChapterBatch(store, http, service, log))
    {
        batch.Scan(); var ready = await Idle(batch);
        Check(Number(ready.GetProperty("summary"), "discs") == 4, "ordinary MKV and loose M2TS excluded");
        Check(Number(ready.GetProperty("summary"), "pending") == 3 && Number(ready.GetProperty("summary"), "preserved") == 1, "missing/generated distinguished from protected custom");
        Check(ready.GetProperty("items").EnumerateArray().Single(i => Text(i, "id") == "shared").GetProperty("libraryIds").GetArrayLength() == 2, "cross-library movie deduplicated");
        Throws(() => batch.Fill(Text(ready,"id"), [], false), "empty selection rejected");
        Throws(() => batch.Fill(Text(ready,"id"), ["other"], false), "unknown library rejected");
        batch.Fill(Text(ready,"id"), ["a"], false); var done = await Idle(batch);
        Check(Number(done,"completed") == 2 && backend.Fills.SequenceEqual(new[] { "bd", "shared" }), "only selected library writes and custom preserved");
        Check(Number(done.GetProperty("summary"),"verified") == 2, "read-back verification updates source status");
        batch.Fill(Text(done,"id"), ["b"], false); done = await Idle(batch);
        Check(Number(done,"completed") == 1 && backend.Fills.Count(id => id == "shared") == 1, "second library handles ISO without rewriting shared disc");
        batch.Fill(Text(done,"id"), ["a"], true); done = await Idle(batch);
        Check(Number(done,"skipped") == 1 && !backend.Fills.Contains("custom"), "recheck still protects custom chapters");
        var snapshot = JsonSerializer.Serialize(batch.Snapshot(), SettingsStore.Json);
        Check(!snapshot.Contains("private-chapter-key") && !snapshot.Contains("/films/"), "snapshot exposes no credentials or source paths");
        Check(!File.ReadAllText(Path.Combine(root,"native-chapter-task.json")).Contains("private-chapter-key"), "persisted plan does not store API key");
        batch.Scan(); ready = await Idle(batch);
        backend.Items["bd"]!["MediaSources"]![0]!["Size"] = 42;
        batch.Fill(Text(ready,"id"), ["a"], true); done = await Idle(batch);
        Check(Number(done,"failed") == 1, "changed source rejected before write");
        batch.Scan(); ready = await Idle(batch); backend.States["bd"] = "generated"; backend.Signatures["bd"] = "manual-change";
        batch.Fill(Text(ready,"id"), ["a"], true); done = await Idle(batch);
        Check(Number(done,"failed") == 1, "chapter edit after scan rejected");
        batch.Scan(); ready = await Idle(batch); backend.BreakReadBack = true;
        batch.Fill(Text(ready,"id"), ["a"], true); done = await Idle(batch);
        Check(Number(done,"failed") > 0, "unconfirmed save cannot count as success"); backend.BreakReadBack = false;
        batch.Scan(); ready = await Idle(batch); backend.Server = "changed-server";
        batch.Fill(Text(ready,"id"), ["a"], true); done = await Idle(batch);
        Check(Text(done,"status") == "failed" && Text(done,"message").Contains("服务器"), "server replacement invalidates plan"); backend.Server = "server";
        batch.Scan(); ready = await Idle(batch); backend.HoldFill = true;
        batch.Fill(Text(ready,"id"), ["a"], true); await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        batch.Cancel(); done = await Idle(batch);
        Check(Text(done,"status") == "cancelled", "cancellation stops running job"); backend.HoldFill = false;
        batch.Fill(Text(done,"id"), ["a"], true); done = await Idle(batch);
        Check(Text(done,"status") == "completed", "cancelled plan can continue");
        await service.Configure(false,"/helper/EmbyBlurayHelper", default);
        Throws(() => batch.Fill(Text(done,"id"), ["a"], true), "disabled switch prevents task writes");
        Check(backend.Signatures["iso"].StartsWith("saved"), "disabling does not clear saved chapters");
    }
    await using (var loaded = new NativeChapterBatch(store,http,service,log))
        Check(Element(loaded.Snapshot()).GetProperty("scanComplete").GetBoolean(), "task progress survives helper restart");
    var planFile = Path.Combine(root,"native-chapter-task.json");
    var file = JsonNode.Parse(File.ReadAllText(planFile))!; file["status"] = "filling"; File.WriteAllText(planFile,file.ToJsonString());
    await using (var loaded = new NativeChapterBatch(store,http,service,log))
        Check(Text(Element(loaded.Snapshot()),"status") == "interrupted", "restart records interruption instead of a stuck running task");
    Check(backend.Authenticated && backend.Buffered, "all Emby calls authenticated and JSON mutations have fixed content length");
    await using (var scoped = new NativeChapterBatch(store,http,service,log))
    {
        var previous = Element(scoped.Snapshot());
        store.Save(store.Current with { EnableNativeChapters = true, ExcludedLibrariesByServer = new() { ["server"] = ["b"] } });
        Check(Element(scoped.Snapshot()).GetProperty("libraries").GetArrayLength() == 0 && Number(Element(scoped.Snapshot()).GetProperty("summary"), "discs") == 0, "chapter snapshots remove stale scope and totals");
        Throws(() => scoped.Fill(Text(previous,"id"), ["b"], true), "chapter fills reject plans made before a scope change");
        scoped.Scan(); var filtered = await Idle(scoped);
        Check(!filtered.GetProperty("libraries").EnumerateArray().Any(l => Text(l,"id") == "b"), "chapter catalog excludes disallowed libraries");
        Throws(() => scoped.Fill(Text(filtered,"id"), ["b"], true), "chapter fill cannot choose an excluded library");
    }
    Console.WriteLine($"Passed {count} native chapter configuration/batch checks.");
}
finally { Directory.Delete(root, true); }

static JsonElement Element(object o) => JsonSerializer.SerializeToElement(o, SettingsStore.Json);
static string Text(JsonElement e,string k) => MediaFeatureCatalog.Text(e,k);
static int Number(JsonElement e,string k) => e.GetProperty(k).GetInt32();
void Check(bool ok,string name) { if(!ok) throw new Exception(name); count++; }
void Throws(Action action,string name) { try { action(); } catch (Exception e) when(e is InvalidOperationException or ArgumentException) { count++; return; } throw new Exception(name); }
async Task ThrowsAsync(Func<Task<object>> action,string name) { try { await action(); } catch (InvalidOperationException) { count++; return; } throw new Exception(name); }
static async Task<JsonElement> Idle(NativeChapterBatch task)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while(true) { var result=Element(task.Snapshot()); if(!result.GetProperty("active").GetBoolean()) return result; await Task.Delay(10,timeout.Token); }
}

sealed class Backend : HttpMessageHandler
{
    internal bool Enabled, IgnoreConfiguration, BreakReadBack, HoldFill;
    internal bool Installed = true, Authenticated = true, Buffered = true;
    internal string Reader = "", Server = "server";
    internal Dictionary<string, JsonNode> Items = new();
    internal Dictionary<string,List<string>> Groups = new() { ["a"] = [], ["b"] = [] };
    internal Dictionary<string,string> States = new(), Signatures = new();
    internal List<string> Fills = [];
    internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Add(string id,string path,string container,string state,params string[] groups)
    {
        Items[id] = new JsonObject { ["Id"] = id, ["Name"] = "影片 " + id, ["MediaSources"] = new JsonArray(new JsonObject {
            ["Id"] = id, ["Path"] = path, ["Container"] = container, ["RunTimeTicks"] = 600_000_000L, ["Size"] = 5_000_000L,
            ["MediaStreams"] = JsonNode.Parse("""[{"Index":0,"Type":"Video","Codec":"hevc"},{"Index":1,"Type":"Audio","Language":"zho","Title":"用户备注"}]""") }) };
        States[id]=state; Signatures[id]="old-"+id; foreach(var group in groups) Groups[group].Add(id);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Authenticated &= request.Headers.TryGetValues("X-Emby-Token",out var values) && values.Single()=="private-chapter-key";
        var path=request.RequestUri!.AbsolutePath;
        if(!Installed && path.Contains("BlurayHelper")) return new(HttpStatusCode.NotFound);
        var body=request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:token);
        if(request.Content is not null) Buffered &= request.Content.Headers.ContentLength > 0;
        object result;
        if(path.EndsWith("/Chapters"))
        {
            if(body.ValueKind==JsonValueKind.Object && !IgnoreConfiguration) { Enabled=body.GetProperty("Enabled").GetBoolean(); Reader=body.GetProperty("ReaderPath").GetString()!; }
            result=new { enabled=Enabled, readerPath=Reader, checkedItems=Fills.Count };
        }
        else if(path.EndsWith("/Inspect")) { var id=body.GetProperty("ItemId").GetString()!; result=new { state=States[id], signature=Signatures[id], count=States[id]=="managed"?3:0, mediaSourceId=id }; }
        else if(path.EndsWith("/Fill"))
        {
            if(HoldFill) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite,token); }
            var id=body.GetProperty("ItemId").GetString()!; Fills.Add(id); States[id]="managed"; Signatures[id]="saved"+Fills.Count;
            result=new { verified=!BreakReadBack, signature=Signatures[id], count=3, playlist="00800.MPLS", seconds=new[]{0,10,20} };
        }
        else if(path.EndsWith("/Info/Public")) result=new { Id=Server };
        else if(path.EndsWith("/VirtualFolders")) result=Groups.Keys.Select(id=>new { ItemId=id,Name="媒体库"+id,CollectionType="" })
            .Append(new { ItemId="collections",Name="合集",CollectionType="boxsets" });
        else if(path.EndsWith("/Items"))
        {
            var q=System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var items=q["Ids"] is { } id ? Items.Where(p=>p.Key==id).Select(p=>p.Value).ToArray() : Groups[q["ParentId"]!].Select(id=>Items[id]).ToArray();
            result=new { Items=items.Skip(int.Parse(q["StartIndex"]??"0")).Take(int.Parse(q["Limit"]??"200")),TotalRecordCount=items.Length };
        }
        else throw new Exception("Unexpected route "+path);
        return new(HttpStatusCode.OK) { Content=JsonContent.Create(result, options: new JsonSerializerOptions()) };
    }
}
