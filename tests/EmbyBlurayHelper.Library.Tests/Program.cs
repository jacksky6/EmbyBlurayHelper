using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.App;

var directory = Path.Combine(Path.GetTempPath(), "bluray-library-check-" + Guid.NewGuid().ToString("N"));
var count = 0;
Directory.CreateDirectory(directory);
try
{
    var settings = new SettingsStore(directory);
    settings.Save(settings.Current with { EmbyApiKey = "private-fixture-key", UpstreamProbePath = "original" });
    using var log = new HelperLog(directory);
    using var backend = new MockEmby();
    using var client = new HttpClient(backend) { Timeout = TimeSpan.FromSeconds(2) };
    var readerSupported = true;
    var prerequisites = new IsoPrerequisites(client, (_, _) => Task.FromResult(new IsoProbeCapability(
        readerSupported ? "ready" : "unsupported", readerSupported ? "HTTP/TCP input supported" : "ffprobe 缺少 http/tcp 输入协议")));
    await using (var task = new LibraryLanguageTask(settings, client, log, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10), prerequisites))
    {
        Check((await task.CheckIsoPrerequisites(default)).CanProbe, "loaded plugin and supported ISO reader pass preflight");
        task.Scan();
        var ready = await Idle(task);
        Check(S(ready,"status") == "ready" && N(ready,"moviesChecked") == 501, "all movie pages checked");
        Check(backend.Offsets.SequenceEqual(new[] {0,200,400}), "Emby pagination uses offsets without dropping movies");
        var summary = ready.GetProperty("summary");
        Check(N(summary,"blurayMovies") == 7 && N(summary,"completeMovies") == 1 && N(summary,"missingMovies") == 5 && N(summary,"unknownMovies") == 1 && N(summary,"unsupportedMovies") == 0, "honest classification includes UHD, 1080p, unknown and ISO");
        Check(N(summary,"missingAudio") == 5 && N(summary,"missingSubtitles") == 5, "external tracks and MKV versions are excluded from missing-language counts");
        Check(log.Entries().Any(e=>e.Source=="LibraryLanguage" && e.Message.Contains("音轨 [#1=und/未识别]") && e.Message.Contains("字幕 [#2=und/未识别]")), "scan logs list audio and subtitle index-to-language mappings");
        var planId = S(ready,"id");
        task.Fill(planId);
        Throws(()=>task.Scan(), "duplicate task rejected");
        var finished = await Idle(task);
        Check(S(finished,"status") == "completed" && N(finished,"processed") == 6, "batch processes all planned movies");
        Check(N(finished,"completed") == 4 && N(finished,"failed") == 2,
            $"valid tracks verified, HTTP failure and disappearing track not counted as success (completed={N(finished,"completed")}, partial={N(finished,"partial")}, failed={N(finished,"failed")}, skipped={N(finished,"skipped")}; items={JsonSerializer.Serialize(finished.GetProperty("items"))})");
        Check(!backend.Writes.Contains("1") && backend.Writes.Contains("4"), "complete movies are skipped and ISO originals are probed");
        Check(backend.Writes.Contains("3") && backend.Writes.Contains("5"), "unknown-track movies and Blu-ray sources behind MKV versions are probed");
        Check(backend.ProbeRequests.Any(request => request.ItemId == "4" && request.SourceId == "source-4"), "ISO is explicitly probed through its Blu-ray source");
        Check(backend.RefreshRequests.Contains("5") && !backend.ProbeRequests.Any(request => request.ItemId == "5"), "BDMV keeps its existing refresh workflow even behind a MKV version");
        Check(backend.ProbeRequests.All(request => request.SourceId == "source-" + request.ItemId), "every batch request uses an explicit Blu-ray media source ID");
        var persisted = await File.ReadAllTextAsync(Path.Combine(directory,"library-language-task.json"));
        Check(!persisted.Contains("private-fixture-key"), "task record stores no API credential");
        Check(log.Entries().Any(e=>e.Source=="LibraryLanguage" && e.Message.Contains("核验")), "planning, item and completion logs available");

        backend.Items = [MockEmby.Movie("40",null),MockEmby.Movie("41",null),MockEmby.Movie("42","eng")];
        backend.Memberships = new() { ["a"]=["40","42"], ["b"]=["40","41"], ["empty"]=[] };
        task.Scan(); ready = await Idle(task);
        Check(ready.GetProperty("libraries").GetArrayLength()==3 && N(ready,"moviesChecked")==3 && N(ready.GetProperty("summary"),"blurayMovies")==3, "per-library scan deduplicates global movies and keeps empty libraries");
        var groups=ready.GetProperty("libraries");
        Check(N(groups[0],"blurayMovies")==2 && N(groups[0],"missingMovies")==1 && N(groups[1],"missingMovies")==2, "each library has its own completion statistics");
        Throws(()=>task.Fill(S(ready,"id"),[]),"empty selection cannot accidentally process every library");
        Throws(()=>task.Fill(S(ready,"id"),["not-in-plan"]),"unknown library selection rejected");
        var multiStart=backend.Writes.Count;
        task.Fill(S(ready,"id"),["a"]); finished=await Idle(task);
        Check(backend.Writes.Skip(multiStart).SequenceEqual(new[]{"40"}) && N(finished,"planned")==1 && S(finished,"scope")=="Library a", "selected library never refreshes movies exclusive to unselected libraries");
        Check(N(finished.GetProperty("libraries")[0],"taskProcessed")==1,"per-library job progress reflects verified work");
        task.Fill(S(finished,"id"),["b"]); finished=await Idle(task);
        Check(N(finished,"planned")==1 && backend.Writes.Last()=="41", "another library can be completed separately without repeating shared completed movies");
        backend.Items=[MockEmby.Movie("40",null),MockEmby.Movie("41",null),MockEmby.Movie("42","eng")];
        backend.ResetPolls();
        task.Scan(); ready=await Idle(task); multiStart=backend.Writes.Count;
        task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"planned")==2 && backend.Writes.Skip(multiStart).Order().SequenceEqual(new[]{"40","41"}),"all libraries process each shared movie exactly once");
        backend.Items=[MockEmby.Movie("50",null)]; backend.Memberships=new(){["a"]=["50"],["b"]=[]};
        task.Scan(); ready=await Idle(task); multiStart=backend.Writes.Count;
        backend.Memberships=new(){["a"]=[],["b"]=["50"]};
        task.Fill(S(ready,"id"),["a"]); finished=await Idle(task);
        Check(N(finished,"skipped")==1 && backend.Writes.Count==multiStart, "fresh scope check skips a movie moved out of the selected library");
        backend.Memberships=new(){["a"]=null};

        backend.Mode = "unchanged"; backend.Items = [MockEmby.Movie("20", null)];
        task.Scan(); ready = await Idle(task);
        task.Fill(S(ready,"id")); finished = await Idle(task);
        Check(N(finished,"completed") == 0 && N(finished,"partial") == 1 && N(finished.GetProperty("summary"),"missingMovies") == 1, "unchanged language fields remain unresolved after timeout");

        backend.Mode = "hold";
        task.Fill(S(finished,"id"));
        await Wait(task, x => x.GetProperty("items")[0].GetProperty("message").GetString()!.StartsWith("Emby 探测已返回"));
        task.Cancel(); var cancelled = await Idle(task);
        Check(S(cancelled,"status") == "cancelled" && N(cancelled,"completed") == 0, "cancellation stops waiting without claiming Emby work cancelled");
        backend.Mode = "normal"; backend.Complete("20"); var writeCount = backend.Writes.Count;
        task.Fill(S(cancelled,"id")); finished = await Idle(task);
        Check(N(finished,"skipped") == 1 && backend.Writes.Count == writeCount, "resume rechecks and skips work already completed by Emby");
        task.Scan(); ready = await Idle(task);
        Check(ready.GetProperty("items")[0].GetProperty("verifiedAt").ValueKind == JsonValueKind.String, "rescan retains helper verification history for unchanged sources");

        settings.Save(settings.Current with { EmbyApiKey = "rotated-key" });
        Throws(()=>task.Fill(S(ready,"id")), "stale connection plan rejected");
        settings.Save(settings.Current with { EmbyApiKey = "private-fixture-key" });
        backend.Items = [MockEmby.Movie("30",null)]; task.Scan(); ready = await Idle(task);
        backend.ProbeConnected = false; writeCount = backend.Writes.Count;
        task.Fill(S(ready,"id")); finished = await Idle(task);
        Check(S(finished,"status") == "failed" && backend.Writes.Count == writeCount, "batch refuses a disconnected probe before submitting refreshes");
        backend.ProbeConnected = true; backend.PluginReady = false;
        backend.Items = [MockEmby.Movie("30",null,"iso",iso:true)];
        task.Scan(); ready = await Idle(task);
        task.Fill(S(ready,"id")); finished = await Idle(task);
        Check(N(finished,"skipped") == 1 && backend.Writes.Count == writeCount && S(finished.GetProperty("items")[0],"message").Contains("插件"), "ISO is not submitted when the plugin is not loaded");
        backend.Items = [MockEmby.Movie("30",null)];
        task.Scan(); ready = await Idle(task); task.Fill(S(ready,"id")); finished = await Idle(task);
        Check(N(finished,"completed") == 1 && backend.RefreshRequests.Contains("30"), "BDMV still works when the new ISO plugin is not installed");
        backend.Items = [MockEmby.Movie("30",null)];
        task.Scan(); finished = await Idle(task); writeCount = backend.Writes.Count;
        backend.PluginReady = true; backend.ServerId = "different-server";
        task.Fill(S(finished,"id")); finished = await Idle(task);
        Check(S(finished,"status") == "failed" && backend.Writes.Count == writeCount, "server identity checked before writing");
        backend.ServerId = "fixture-server"; backend.Mode = "hold";
        task.Fill(S(finished,"id"));
        await Wait(task,x=>x.GetProperty("items")[0].GetProperty("message").GetString()!.StartsWith("Emby 探测已返回"));
        settings.Save(settings.Current with { EmbyApiKey = "rotated-key" });
        finished = await Idle(task);
        Check(S(finished,"status") == "failed" && N(finished,"completed") == 0, "settings changes stop an active task");
        settings.Save(settings.Current with { EmbyApiKey = "private-fixture-key" });

        backend.Mode = "normal"; backend.ResetPolls();
        var multiIso = MockEmby.Movie("60",null,"iso",iso:true);
        var secondSource = multiIso["MediaSources"]![0]!.DeepClone();
        secondSource["Id"] = "source-60-alt"; secondSource["Path"] = "/discs/60-alt.iso";
        multiIso["MediaSources"]!.AsArray().Add(secondSource);
        backend.Items = [multiIso]; task.Scan(); ready = await Idle(task);
        task.Fill(S(ready,"id")); finished = await Idle(task);
        Check(N(finished,"completed") == 1 && backend.ProbeRequests.Where(request => request.ItemId == "60")
            .Select(request => request.SourceId).Order().SequenceEqual(new[] { "source-60", "source-60-alt" }),
            "all incomplete Blu-ray versions are explicitly probed, not only the first version");

        // Existing languages must not hide missing duration, even when only one version lacks it.
        var runtimeOnly = MockEmby.Movie("61","eng","blurayiso",iso:true);
        var runtimeAlt = runtimeOnly["MediaSources"]![0]!.DeepClone();
        runtimeAlt["Id"]="source-61-alt"; runtimeAlt["Path"]="/discs/61-alt.iso"; runtimeAlt["RunTimeTicks"]=null;
        runtimeOnly["RunTimeTicks"]=99_000_000_000L; // Parent duration must not mask a version's missing duration.
        runtimeOnly["MediaSources"]!.AsArray().Add(runtimeAlt);
        backend.Items=[runtimeOnly]; task.Scan(); ready=await Idle(task);
        Check(N(ready.GetProperty("summary"),"missingRuntimeMovies")==1 && N(ready.GetProperty("summary"),"unknownMovies")==1 &&
            N(ready.GetProperty("items")[0],"missingAudio")==0, "runtime-only omissions enter the plan with languages intact");
        backend.RuntimeCapable=false; writeCount=backend.Writes.Count;
        task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"skipped")==1 && backend.Writes.Count==writeCount, "old plugin is rejected before requesting duration completion");
        backend.RuntimeCapable=true;
        task.Fill(S(finished,"id")); finished=await Idle(task);
        Check(N(finished,"completed")==1 && backend.ProbeRequests.Where(r=>r.ItemId=="61").Single().SourceId=="source-61-alt",
            "runtime completion probes only the missing version and verifies saved duration");
        Check(finished.GetProperty("items")[0].GetProperty("sources").EnumerateArray().All(s=>s.GetProperty("runTimeTicks").GetInt64()>0),
            "task response includes each version's saved duration for display");

        var unresolved = MockEmby.Movie("62","eng","iso",iso:true);
        unresolved["MediaSources"]![0]!["RunTimeTicks"]=0L;
        backend.Items=[unresolved]; backend.Mode="unchanged"; task.Scan(); ready=await Idle(task);
        task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"completed")==0 && N(finished,"partial")==1 && N(finished.GetProperty("items")[0],"missingRuntime")==1,
            "probe response cannot substitute for a duration actually saved by Emby");
        backend.Mode="normal";

        backend.PluginStatusCode=HttpStatusCode.NotFound;
        var isoCheck=await task.CheckIsoPrerequisites(default);
        Check(!isoCheck.CanProbe && isoCheck.PluginState=="not-loaded" && isoCheck.Message.Contains("重启 Emby"),
            "missing endpoint warns about installation, restart and permissions without claiming a definite cause");
        backend.PluginStatusCode=HttpStatusCode.Unauthorized;
        isoCheck=await task.CheckIsoPrerequisites(default);
        Check(isoCheck.PluginState=="unknown" && isoCheck.Message.Contains("401"), "API authentication failure is not misreported as a missing DLL");
        backend.PluginStatusCode=HttpStatusCode.OK;
        backend.RuntimeCapable=false;
        Check((await task.CheckIsoPrerequisites(default)).PluginState=="outdated", "loaded old plugin needs update and restart");
        backend.RuntimeCapable=true;
        readerSupported=false;
        backend.Items=[MockEmby.Movie("63",null,"iso",iso:true),MockEmby.Movie("64",null,"iso",iso:true),MockEmby.Movie("65",null)];
        task.Scan(); ready=await Idle(task); writeCount=backend.Writes.Count;
        var checksBefore=backend.PluginChecks;
        task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"skipped")==2 && N(finished,"completed")==1 && backend.Writes.Skip(writeCount).SequenceEqual(new[]{"65"}),
            "unsupported ffprobe skips ISO without probing while BDMV remains usable");
        Check(backend.PluginChecks==checksBefore+1 && log.Entries().Any(e=>e.Level=="Warning" && e.Message.Contains("ISO 前置检查未通过")),
            "batch checks prerequisites once and records a clear warning before movie submissions");
        readerSupported=true;
        Check((await task.CheckIsoPrerequisites(default)).CanProbe, "portable reader passes prerequisites with HTTP/TCP support");
        task.Scan(); ready=await Idle(task); task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"completed")==2 && N(finished,"skipped")==0, "reader-enabled ISO movies are submitted and verified instead of skipped");
        backend.ProbeFailure=true;
        backend.Items=[MockEmby.Movie("66",null,"iso",iso:true)];
        task.Scan(); ready=await Idle(task); task.Fill(S(ready,"id")); finished=await Idle(task);
        var errorMessage=S(finished.GetProperty("items")[0],"message");
        Check(errorMessage.Contains("POST /emby/BlurayHelper/Probe") && errorMessage.Contains("Protocol not found") &&
            !errorMessage.Contains("private-fixture-key"), "probe errors expose stage and redacted Emby response instead of generic HTTP 500");
        backend.ProbeFailure=false;

        backend.DirectBdmv=true;
        backend.Items=[MockEmby.Movie("68",null,unknown:true)];
        task.Scan(); ready=await Idle(task); task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"completed")==1 && backend.ProbeRequests.Any(r=>r.ItemId=="68") &&
            !backend.RefreshRequests.Contains("68"), "new plugin probes BDMV directly without the legacy metadata refresh");
        backend.Items=[MockEmby.Movie("69",null)]; backend.Mode="unchanged";
        task.Scan(); ready=await Idle(task); task.Fill(S(ready,"id")); finished=await Idle(task);
        Check(N(finished,"partial")==1 && S(finished.GetProperty("items")[0],"message").Contains("und"),
            "unmarked source languages remain unresolved and are explained without guessing");
        backend.Mode="normal"; backend.DirectBdmv=false;

        // Rechecking complete discs must reach the probe, including every disc version.
        var priorItems = backend.Items;
        var completeIso = MockEmby.Movie("70","eng","iso",iso:true);
        var completeIsoAlt = completeIso["MediaSources"]![0]!.DeepClone();
        completeIsoAlt["Id"]="source-70-alt"; completeIsoAlt["Path"]="/discs/70-alt.iso";
        completeIso["MediaSources"]!.AsArray().Add(completeIsoAlt);
        var completeBdmv = MockEmby.Movie("72","eng",mixed:true);
        completeBdmv["MediaSources"]![1]!["MediaStreams"]![0]!["Width"]=1920;
        var completeBdmvAlt = completeBdmv["MediaSources"]![1]!.DeepClone();
        completeBdmvAlt["Id"]="source-72-alt"; completeBdmvAlt["Path"]="/discs/72-alt";
        completeBdmv["MediaSources"]!.AsArray().Add(completeBdmvAlt);
        var unknownResolution = MockEmby.Movie("71","eng");
        unknownResolution["MediaSources"]![0]!["MediaStreams"]![0]!["Width"]=0;
        backend.Items=[completeIso,completeBdmv,unknownResolution];
        backend.Memberships=new(){["a"]=["70","72"],["b"]=["70","71"]};
        task.Scan(); ready=await Idle(task);
        summary=ready.GetProperty("summary");
        Check(N(ready,"moviesChecked")==3 && N(summary,"blurayMovies")==3 && N(summary,"uhdMovies")==1 &&
            N(summary,"fullHdMovies")==1 && N(summary,"unclassifiedMovies")==1,
            "resolution summary deduplicates movies and separates UHD, 1080P and unknown discs");
        Throws(()=>task.Fill(S(ready,"id"),["a"]), "complete discs remain excluded by default");
        Throws(()=>task.Fill(S(ready,"id"),[],true), "include-verified cannot turn an empty scope into all libraries");
        Throws(()=>task.Fill(S(ready,"id"),["not-in-plan"],true), "include-verified still rejects unknown libraries");
        writeCount=backend.Writes.Count;
        task.Fill(S(ready,"id"),["a"],true); finished=await Idle(task);
        Check(N(finished,"planned")==2 && N(finished,"completed")==2 && N(finished,"skipped")==0 &&
            backend.Writes.Skip(writeCount).Order().SequenceEqual(new[]{"70","70","72"}),
            "include-verified reprobes complete ISO versions and refreshes complete legacy BDMV within the selected library");
        Check(finished.GetProperty("includeVerified").GetBoolean() &&
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"library-language-task.json")))!["includeVerified"]!.GetValue<bool>() &&
            log.Entries().Any(e=>e.Source=="LibraryLanguage" && e.Message.Contains("含已核验的")),
            "the recheck mode is returned, persisted and logged");
        backend.DirectBdmv=true;
        var probeStart=backend.ProbeRequests.Count;
        task.Fill(S(finished,"id"),["a"],true); finished=await Idle(task);
        Check(N(finished,"completed")==2 && backend.ProbeRequests.Skip(probeStart).Select(r=>r.SourceId).Order()
            .SequenceEqual(new[]{"source-70","source-70-alt","source-72","source-72-alt"}),
            "direct recheck probes every complete BDMV and ISO source while excluding MKV versions and unselected movies");
        backend.ProbeFailure=true;
        task.Fill(S(finished,"id"),["a"],true); finished=await Idle(task);
        Check(N(finished,"failed")==2 && N(finished,"completed")==0, "saved complete languages cannot hide a failed recheck");
        backend.ProbeFailure=false;
        probeStart=backend.ProbeRequests.Count;
        task.Fill(S(finished,"id"),["a","b"],true); finished=await Idle(task);
        Check(N(finished,"planned")==3 && N(finished,"completed")==3 && backend.ProbeRequests.Skip(probeStart).Count(r=>r.ItemId=="70")==2,
            "include-verified across libraries processes a shared multi-version movie only once");
        Throws(()=>task.Fill(S(finished,"id")), "a later default fill does not silently inherit include-verified mode");
        backend.Items=priorItems; backend.Memberships=new(){["a"]=null}; backend.DirectBdmv=false;

        backend.FailPagination = true; task.Scan(); var broken = await Idle(task);
        Check(S(broken,"status") == "failed" && !broken.GetProperty("scanComplete").GetBoolean(), "incomplete list is not offered as a valid plan");
        backend.FailPagination = false; task.Scan(); await Idle(task);
    }

    // Upgrade a real persisted verification record from the original ISO reader.
    var legacyPath=Path.Combine(directory,"library-language-task.json");
    var legacyPlan=JsonNode.Parse(await File.ReadAllTextAsync(legacyPath))!;
    var legacyMovie=LibraryLanguageTask.Analyze(JsonSerializer.SerializeToElement(MockEmby.Movie("legacy","eng","iso",iso:true)))!;
    legacyMovie.VerifiedAt=legacyMovie.AttemptedAt=DateTimeOffset.UtcNow.AddDays(-1);
    legacyMovie.LibraryIds=["a"];
    legacyPlan["items"]=JsonSerializer.SerializeToNode(new[]{legacyMovie},SettingsStore.Json);
    await File.WriteAllTextAsync(legacyPath,legacyPlan.ToJsonString());
    backend.Items=[MockEmby.Movie("legacy","eng","iso",iso:true)];
    await using(var upgraded=new LibraryLanguageTask(settings,client,log,isoPrerequisites:prerequisites))
    {
        upgraded.Scan();var ready=await Idle(upgraded);
        Check(ready.GetProperty("items")[0].GetProperty("needsPlaylistRepair").GetBoolean() && N(ready.GetProperty("summary"),"unknownMovies")==1,
            "previously verified legacy ISO is planned for explicit main-playlist repair despite nonzero runtime");
        upgraded.Fill(S(ready,"id"));var done=await Idle(upgraded);
        Check(N(done,"skipped")==1 && S(done.GetProperty("items")[0],"message").Contains("0.1.2"),
            "legacy ISO repair waits for a plugin with explicit repair capability");
        backend.PlaylistRepair=true;
        upgraded.Fill(S(done,"id"));done=await Idle(upgraded);
        Check(N(done,"completed")==1 && backend.RepairRequests.Contains("legacy"), "legacy ISO sends explicit repair flag to refresh wrong-title tracks and runtime");
        upgraded.Scan();done=await Idle(upgraded);
        Check(!done.GetProperty("items")[0].GetProperty("needsPlaylistRepair").GetBoolean() &&
            N(done.GetProperty("summary"),"completeMovies")==1, "successful reader revision is persisted and not repaired repeatedly");
    }

    Check(IsoPrerequisites.ClassifyProtocols("Input:\n file\n bluray\nOutput:\n file").State == "unsupported", "native bluray alone cannot enable the unified reader");
    Check(IsoPrerequisites.ClassifyProtocols("Input:\n file\nOutput:\n http\n tcp").State == "unsupported", "output-only protocols cannot enable reading");
    Check(IsoPrerequisites.ClassifyProtocols("configuration: --enable-protocol=http\nInput:\n file").State == "unsupported", "build flags alone cannot prove input support");
    Check(IsoPrerequisites.ClassifyProtocols("Input:\n file\n http\n tcp\nOutput:\n file").State == "ready",
        "the unified ISO reader needs HTTP/TCP input, regardless of native bluray support");
    Check(IsoPrerequisites.ClassifyProtocols("Input:\n file\nOutput:\n file").State == "unsupported",
        "a native ffprobe without bluray or HTTP/TCP remains unsupported");

    foreach (var container in new[] {"iso","blurayiso"})
    {
        using var item = JsonDocument.Parse(MockEmby.Movie("iso-"+container,null,container,iso:true).ToJsonString());
        var analyzed = LibraryLanguageTask.Analyze(item.RootElement);
        Check(analyzed is not null && analyzed.Classification == "missing" && analyzed.Sources.Count == 1,
            $"Emby {container} sources join the shared original-disc completion workflow");
    }

    var lowResolution=MockEmby.Movie("resolution","eng");
    lowResolution["MediaSources"]![0]!["MediaStreams"]![0]!["Width"]=1280;
    Check(LibraryLanguageTask.Analyze(JsonSerializer.SerializeToElement(lowResolution))!.Format=="待确认",
        "a non-UHD resolution is not automatically counted as 1080P");
    lowResolution["MediaSources"]![0]!["MediaStreams"]![0]!["Width"]=1440;
    lowResolution["MediaSources"]![0]!["MediaStreams"]![0]!["Height"]=1080;
    Check(LibraryLanguageTask.Analyze(JsonSerializer.SerializeToElement(lowResolution))!.Format=="Blu-ray",
        "1080-line Blu-ray with a non-square-pixel width is still classified as full HD");

    // Simulate a crash using a real persisted active state, without an automatic resume on startup.
    var path = Path.Combine(directory,"library-language-task.json");
    var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
    saved["status"] = "filling"; saved["scanComplete"] = true;
    saved["items"]![0]!["result"] = "running";
    await File.WriteAllTextAsync(path,saved.ToJsonString());
    var writesBeforeRestart = backend.Writes.Count;
    await using (var restarted = new LibraryLanguageTask(settings, client, log))
    {
        var state = State(restarted);
        Check(S(state,"status") == "interrupted" && backend.Writes.Count == writesBeforeRestart, "restart preserves plan and marks interrupted without submitting work");
        Check(S(state.GetProperty("items")[0],"result") == "unverified", "interrupted item never becomes a successful item");
    }
    using (var scopedBackend = new MockEmby { Items = [MockEmby.Movie("901", null), MockEmby.Movie("902", null)],
        Memberships = new() { ["a"] = ["901"], ["b"] = ["902"] } })
    using (var scopedClient = new HttpClient(scopedBackend))
    await using (var scopedTask = new LibraryLanguageTask(settings, scopedClient, log))
    {
        scopedTask.Scan(); var originalPlan = await Idle(scopedTask);
        scopedTask.Fill(S(originalPlan,"id"), ["b"]); originalPlan = await Idle(scopedTask);
        settings.Save(settings.Current with { ExcludedLibrariesByServer = new() { ["fixture-server"] = ["b"] } });
        var stale = State(scopedTask);
        Check(!stale.GetProperty("connectionMatches").GetBoolean() && stale.GetProperty("libraries").GetArrayLength() == 0 && stale.GetProperty("items").GetArrayLength() == 0,
            "changing global scope hides stale libraries and film rows");
        Check(N(stale, "moviesChecked") == 0 && N(stale.GetProperty("summary"), "blurayMovies") == 0, "stale scope does not contaminate totals");
        Throws(() => scopedTask.Fill(S(originalPlan,"id"), ["b"]), "old plan cannot process a newly excluded library");
        scopedTask.Scan(); var scoped = await Idle(scopedTask);
        Check(scoped.GetProperty("libraries").GetArrayLength() == 1 && N(scoped,"moviesChecked") == 1 && S(scoped.GetProperty("items")[0],"id") == "901", "language checks enumerate allowed libraries only");
        Throws(() => scopedTask.Fill(S(scoped,"id"), ["b"]), "direct fill request cannot choose an excluded library");
        settings.Save(settings.Current with { ExcludedLibrariesByServer = new() { ["different-server"] = ["b"] } });
        scopedTask.Scan(); scoped = await Idle(scopedTask);
        Check(scoped.GetProperty("libraries").GetArrayLength() == 2, "another server's exclusions do not hide this server's libraries");
        Check(scoped.GetProperty("items").EnumerateArray().Single(i => S(i,"id") == "902").GetProperty("verifiedAt").ValueKind == JsonValueKind.String,
            "excluding and restoring a library retains verification history for unchanged discs");
    }
    Console.WriteLine($"Passed {count} library task checks.");
}
finally { Directory.Delete(directory,true); }

void Check(bool condition,string name) { if(!condition) throw new Exception(name); count++; }
void Throws(Action action,string name) { try { action(); } catch(InvalidOperationException) {count++;return;} throw new Exception(name); }
static JsonElement State(LibraryLanguageTask task) => JsonSerializer.SerializeToElement(task.Snapshot(),SettingsStore.Json);
static string S(JsonElement value,string name) => value.GetProperty(name).GetString() ?? "";
static int N(JsonElement value,string name) => value.GetProperty(name).GetInt32();
static Task<JsonElement> Idle(LibraryLanguageTask task) => Wait(task,x=>!x.GetProperty("active").GetBoolean());
static async Task<JsonElement> Wait(LibraryLanguageTask task,Func<JsonElement,bool> predicate)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
    while(DateTimeOffset.UtcNow<deadline)
    { var state=State(task); if(predicate(state))return state; await Task.Delay(5); }
    throw new Exception("Task did not reach expected state: "+State(task));
}

sealed class MockEmby : HttpMessageHandler
{
    private readonly object gate = new();
    public List<int> Offsets {get;} = [];
    public List<string> Writes {get;} = [];
    public List<(string ItemId, string SourceId)> ProbeRequests {get;} = [];
    public List<string> RefreshRequests {get;} = [];
    public List<JsonObject> Items {get;set;}
    public Dictionary<string,HashSet<string>?> Memberships {get;set;} = new() { ["a"] = null };
    public string Mode {get;set;} = "normal";
    public bool ProbeConnected {get;set;} = true;
    public bool PluginReady {get;set;} = true;
    public bool RuntimeCapable {get;set;} = true;
    public bool DirectBdmv {get;set;}
    public bool PlaylistRepair {get;set;}
    public List<string> RepairRequests {get;}=[];
    public HttpStatusCode PluginStatusCode {get;set;} = HttpStatusCode.OK;
    public int PluginChecks {get;private set;}
    public bool ProbeFailure {get;set;}
    public bool FailPagination {get;set;}
    public string ServerId {get;set;} = "fixture-server";
    private readonly Dictionary<string,int> polls = [];
    public void ResetPolls() { lock(gate) polls.Clear(); }
    public MockEmby()
    {
        Items = Enumerable.Range(100,494).Select(i=>Movie(i.ToString(),"eng","mkv")).ToList();
        Items.InsertRange(0,[Movie("1","eng"),Movie("2",null),Movie("3",null,unknown:true),Movie("4",null,"iso",iso:true),Movie("5",null,mixed:true),Movie("6",null),Movie("7",null)]);
    }
    public static JsonObject Movie(string id,string? language,string container="bluray",bool unknown=false,bool iso=false,bool mixed=false)
    {
        var streams = new JsonArray();
        streams.Add(new JsonObject { ["Type"]="Video",["Index"]=0,["Width"]=id=="2"?1920:3840 });
        if(!unknown)
        {
            streams.Add(new JsonObject {["Type"]="Audio",["Index"]=1,["Language"]=language});
            streams.Add(new JsonObject {["Type"]="Subtitle",["Index"]=2,["Language"]=language});
        }
        streams.Add(new JsonObject {["Type"]="Subtitle",["Index"]=3,["IsExternal"]=true});
        var source = new JsonObject {["Id"]="source-"+id,["Path"]="/discs/"+id+(iso?".iso":""),["Container"]=container,["RunTimeTicks"]=72_000_000_000L,["MediaStreams"]=streams};
        var sources = new JsonArray();
        if(mixed) sources.Add(new JsonObject {["Id"]="mkv-"+id,["Path"]="/files/"+id+".mkv",["Container"]="mkv",["MediaStreams"]=new JsonArray(new JsonObject {["Type"]="Audio",["Index"]=1})});
        sources.Add(source);
        return new JsonObject {["Id"]=id,["Name"]="Movie <"+id+">",["Container"]=mixed?"mkv":container,["MediaSources"]=sources};
    }
    public void Complete(string id)
    {
        lock(gate)
        {
            var item=Items.Single(i=>i["Id"]!.GetValue<string>()==id);
            foreach(var source in item["MediaSources"]!.AsArray().OfType<JsonObject>().Where(s=>s["Container"]!.GetValue<string>() is "bluray" or "iso" or "blurayiso"))
            {
                if(source["RunTimeTicks"] is null || source["RunTimeTicks"]!.GetValue<long>() <= 0) source["RunTimeTicks"]=72_000_000_000L;
                var tracks=source["MediaStreams"]!.AsArray();
                if(!tracks.OfType<JsonObject>().Any(t=>t["Type"]!.GetValue<string>()=="Audio"))
                    tracks.Add(new JsonObject {["Type"]="Audio",["Index"]=1,["Language"]="eng"});
                foreach(var track in tracks.OfType<JsonObject>().Where(t=>t["Type"]!.GetValue<string>() is "Audio" or "Subtitle")) track["Language"]="eng";
            }
        }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!request.Headers.TryGetValues("X-Emby-Token",out var keys) || keys.Single()!="private-fixture-key") return new(HttpStatusCode.Unauthorized);
        var path=request.RequestUri!.AbsolutePath;
        if(path.EndsWith("System/Info/Public"))return Json(new {Id=ServerId});
        if(path.EndsWith("Library/VirtualFolders"))return Json(Memberships.Keys.Select(id=>new {ItemId=id,Name="Library "+id}).ToArray());
        if(path.EndsWith("Encoding/FfmpegOptions"))return Json(new {Object=new {UseCustomProbePath=ProbeConnected,CustomProbePath=Environment.ProcessPath}});
        if(path.EndsWith("BlurayHelper/Status"))
        {
            PluginChecks++;
            var capabilities=new List<string>();
            if(RuntimeCapable)capabilities.Add("missing-runtime");
            if(DirectBdmv)capabilities.Add("bdmv-direct-probe");
            if(PlaylistRepair)capabilities.Add("playlist-repair");
            return PluginStatusCode==HttpStatusCode.OK ? Json(new {status=PluginReady?"ready":"unavailable",version="0.1.1",capabilities}) : new(PluginStatusCode);
        }
        if(path.EndsWith("BlurayHelper/Probe"))
        {
            // Real Emby 4.10 receives an empty DTO when JSON is sent chunked.
            if(request.Content?.Headers.ContentLength is null or <= 0) return new(HttpStatusCode.BadRequest);
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            // Match the real Emby serializer: camelCase fields are not bound to this DTO.
            if(body["ItemId"] is null || body["MediaSourceId"] is null)
                return new(HttpStatusCode.BadRequest);
            var id=body["ItemId"]!.GetValue<string>();
            var sourceId=body["MediaSourceId"]!.GetValue<string>();
            if(body["RepairPlaylist"]?.GetValue<bool>()==true)RepairRequests.Add(id);
            if(ProbeFailure) return new(HttpStatusCode.InternalServerError) {Content=new StringContent("ffprobe failed: bluray: Protocol not found; api_key=private-fixture-key")};
            lock(gate){Writes.Add(id);ProbeRequests.Add((id,sourceId));polls[id]=0;}
            if(id=="6") return new(HttpStatusCode.InternalServerError);
            if(Mode == "normal") Complete(id);
            return Json(new {itemId=id,sourceId,streamCount=3,runTimeTicks=72_000_000_000L,audio=new[]{new {index=1,language="eng"}},subtitles=new[]{new {index=2,language="eng"}}});
        }
        if(path.EndsWith("/Refresh"))
        {
            var id=path.Split('/')[3];
            lock(gate){Writes.Add(id);RefreshRequests.Add(id);polls[id]=0;}
            return new(id=="6"?HttpStatusCode.InternalServerError:HttpStatusCode.NoContent);
        }
        if(path.EndsWith("/Items"))
        {
            var q=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            lock(gate)
            {
                if(q.TryGetValue("Ids",out var ids))
                {
                    var id=ids.ToString();
                    if(polls.ContainsKey(id) && ++polls[id]>=3 && Mode=="normal" && id!="6")
                    {
                        Complete(id);
                        if(id=="7")
                        {
                            var tracks=Items.Single(i=>i["Id"]!.GetValue<string>()==id)["MediaSources"]![0]!["MediaStreams"]!.AsArray();
                            var missing=tracks.OfType<JsonObject>().FirstOrDefault(t=>t["Index"]!.GetValue<int>()==2);
                            if(missing is not null) tracks.Remove(missing);
                        }
                    }
                    return Json(new {Items=Items.Where(i=>i["Id"]!.GetValue<string>()==id).ToArray()});
                }
                var offset=int.Parse(q["StartIndex"].ToString()); Offsets.Add(offset);
                var parent=q["ParentId"].ToString();
                if(!Memberships.TryGetValue(parent,out var membership))return new(HttpStatusCode.NotFound);
                var selected=Items.Where(i=>membership is null || membership.Contains(i["Id"]!.GetValue<string>())).ToArray();
                return Json(new {Items=FailPagination?Array.Empty<JsonObject>():selected.Skip(offset).Take(200).ToArray(),TotalRecordCount=selected.Length});
            }
        }
        return new(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Json(object value)=> new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
}
