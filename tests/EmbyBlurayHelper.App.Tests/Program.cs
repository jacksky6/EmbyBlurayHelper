using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

if (args.Length == 1 && args[0] == "-version") { Console.WriteLine("ffprobe version fixture"); return; }
if (args.SequenceEqual(new[] { "-hide_banner", "-protocols" })) { Console.WriteLine("Supported file protocols:\nInput:\n file\n http\n tcp\nOutput:\n file"); return; }
if (args.Length != 1) throw new ArgumentException("Pass the published helper executable path.");
var executable = Path.GetFullPath(args[0]);
var directory = Path.Combine(Path.GetTempPath(), "bluray-helper-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var adminPort = Port();
var proxyPort = Port();
var backendPort = Port();
var options = new { managementUrl = $"http://127.0.0.1:{adminPort}", proxyUrl = $"http://127.0.0.1:{proxyPort}", embyUrl = $"http://127.0.0.1:{backendPort}", upstreamProbePath = Environment.ProcessPath!, embyApiKey = "test-secret", enableProbeLanguagePatch = true, enableInfuseLanguagePatch = true };
await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(options));
await File.WriteAllTextAsync(Path.Combine(directory, "management-key.txt"), "legacy-management-key-preserved");
using var backend = new HttpListener();
backend.Prefixes.Add($"http://127.0.0.1:{backendPort}/"); backend.Start();
using var cancellation = new CancellationTokenSource();
var mockProbeEnabled = false;
var mockProbePath = "original";
string? mockOriginalProbePath = Environment.ProcessPath;
var mockFailure = false;
var mockRejectMutation = false;
var mockIgnoreMutation = false;
var mockWrites = 0;
var mockPluginLoaded = true;
var mockFeatures = false;
var mockTrackTitles = false;
var mockIgnoreFeatures = false;
var featureRequestsBuffered = true;
var featureProbe = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "feature-probe-fixture-" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
File.Copy(Environment.ProcessPath!, featureProbe);
var backendTask = Task.Run(async () =>
{
    while (!cancellation.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await backend.GetContextAsync().WaitAsync(cancellation.Token); } catch (OperationCanceledException) { break; }
        if (context.Request.Url!.AbsolutePath == "/emby/Encoding/FfmpegOptions" && context.Request.Headers["X-Emby-Token"] != "test-secret")
        { context.Response.StatusCode = 401; context.Response.Close(); continue; }
        if (mockFailure) { context.Response.StatusCode = 503; context.Response.Close(); continue; }
        if (context.Request.Url.AbsolutePath.StartsWith("/emby/BlurayHelper/") && !mockPluginLoaded)
        { context.Response.StatusCode = 404; context.Response.Close(); continue; }
        if (context.Request.Url.AbsolutePath == "/emby/BlurayHelper/MediaFeatures" && context.Request.HttpMethod == "POST")
        {
            featureRequestsBuffered &= context.Request.ContentLength64 > 0 && context.Request.Headers["Transfer-Encoding"] is null;
            var changed = (await JsonDocument.ParseAsync(context.Request.InputStream)).RootElement;
            if (!mockIgnoreFeatures) mockFeatures = changed.GetProperty("Enabled").GetBoolean();
            if (changed.TryGetProperty("ShowOriginalTrackTitles", out var titles) && titles.ValueKind is JsonValueKind.True or JsonValueKind.False)
                mockTrackTitles = titles.GetBoolean();
        }
        if (context.Request.Url.AbsolutePath == "/emby/Encoding/FfmpegOptions" && context.Request.HttpMethod == "POST")
        {
            mockWrites++;
            if (mockRejectMutation) { context.Response.StatusCode = 500; context.Response.Close(); continue; }
            var changed = (await JsonDocument.ParseAsync(context.Request.InputStream)).RootElement;
            if (changed.GetProperty("UntouchedSetting").GetString() != "keep") throw new Exception("Other encoding settings must be preserved");
            if (!mockIgnoreMutation)
            {
                mockProbeEnabled = changed.GetProperty("UseCustomProbePath").GetBoolean();
                mockProbePath = changed.GetProperty("CustomProbePath").GetString()!;
            }
        }
        var text = context.Request.Url!.AbsolutePath == "/emby/System/Info/Public" ? "{\"ServerName\":\"Test Emby\",\"Version\":\"4.10.2.0\"}" :
            context.Request.Url.AbsolutePath == "/emby/BlurayHelper/Status" ? "{\"status\":\"ready\",\"version\":\"0.1.1\",\"capabilities\":[\"media-streams\",\"missing-runtime\"]}" :
            context.Request.Url.AbsolutePath == "/emby/BlurayHelper/MediaFeatures" ? JsonSerializer.Serialize(new { enabled = mockFeatures, originalTrackTitles = mockTrackTitles, checkedItems = 2 }) :
            context.Request.Url.AbsolutePath == "/emby/Encoding/FfmpegOptions" ? JsonSerializer.Serialize(new { Object = new { UseCustomProbePath = mockProbeEnabled, CustomProbePath = mockProbePath, OriginalProbePath = mockOriginalProbePath, UntouchedSetting = "keep" } }) : "passthrough";
        context.Response.ContentType = context.Request.Url.AbsolutePath.StartsWith("/emby/") ? "application/json" : "text/plain";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text); context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }
});
using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
process.StartInfo.ArgumentList.Add("--serve"); process.StartInfo.ArgumentList.Add("--data-dir"); process.StartInfo.ArgumentList.Add(directory);
process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
var count = 0;
try
{
    var cookies = new CookieContainer();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(options.managementUrl) };
    for (var retry = 0; ; retry++)
    {
        try { if ((await client.GetAsync("/")).IsSuccessStatusCode) break; }
        catch (HttpRequestException) when (retry < 100) { }
        if (retry >= 100 || process.HasExited) throw new Exception("Helper did not start: " + await stderr);
        await Task.Delay(100);
    }
    Check((await client.GetAsync("api/settings")).StatusCode == HttpStatusCode.Unauthorized, "management requires login");
    Check((await client.GetAsync("api/account")).StatusCode == HttpStatusCode.Unauthorized, "account settings require login");
    Check((await client.GetAsync("api/probe/status")).StatusCode == HttpStatusCode.Unauthorized, "probe status requires login");
    Check((await client.GetAsync("api/library-language")).StatusCode == HttpStatusCode.Unauthorized, "library task status requires login");
    Check((await client.GetAsync("api/library-scope")).StatusCode == HttpStatusCode.Unauthorized, "global scope requires login");
    Check((await client.PostAsJsonAsync("api/library-scope", new { serverId = "one", excludedLibraryIds = new[] { "a" } })).StatusCode == HttpStatusCode.Unauthorized, "anonymous requests cannot change processing scope");
    Check((await client.GetAsync("/library-scope.js")).IsSuccessStatusCode, "global scope UI script is embedded and served");
    Check((await client.GetAsync("api/iso/status")).StatusCode == HttpStatusCode.Unauthorized, "ISO readiness requires management login");
    Check((await client.GetAsync("api/media-features")).StatusCode == HttpStatusCode.Unauthorized, "supplementary feature status requires management login");
    Check((await client.GetAsync("api/media-features/batch")).StatusCode == HttpStatusCode.Unauthorized, "feature batch progress requires login");
    Check((await client.PostAsJsonAsync("api/media-features/batch/scan", new { })).StatusCode == HttpStatusCode.Unauthorized, "feature batch scan requires login");
    Check((await client.GetAsync("/library.js")).IsSuccessStatusCode && (await client.GetAsync("/media-features.js")).StatusCode == HttpStatusCode.NotFound, "media library UI script is embedded and served; removed scripts are gone");
    Check((await client.GetAsync("/chapters.js")).IsSuccessStatusCode, "native chapter UI script is embedded and served");
    var updateManifestResponse = await client.GetAsync("/plugin-update/manifest.json");
    var updateManifestJson = await updateManifestResponse.Content.ReadAsStringAsync();
    Check(updateManifestResponse.IsSuccessStatusCode, "embedded plugin update manifest is available: " + (updateManifestResponse.IsSuccessStatusCode ? "OK" : updateManifestJson));
    var updateManifest = JsonDocument.Parse(updateManifestJson).RootElement;
    var updateBinary = await client.GetByteArrayAsync("/plugin-update/EmbyBlurayHelper.Plugin.dll");
    Check(updateManifest.GetProperty("assemblyName").GetString() == "EmbyBlurayHelper.Plugin" && updateManifest.GetProperty("version").GetString() == "0.1.10" &&
        updateManifest.GetProperty("schemaVersion").GetInt32() == 1 && updateManifest.GetProperty("size").GetInt64() == updateBinary.Length &&
        updateManifest.GetProperty("sha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(updateBinary)).ToLowerInvariant() &&
        updateBinary.AsSpan(0, 2).SequenceEqual(new byte[] { 77, 90 }), "anonymous update source serves only the matching embedded plugin and verified manifest");
    Check((await client.GetAsync("/plugin-update/settings.json")).StatusCode == HttpStatusCode.NotFound, "update source cannot expose arbitrary workstation files");
    foreach (var (route, type, signature) in new[] {
        ("/emby-icon.png", "image/png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ("/infuse-icon.jpg", "image/jpeg", new byte[] { 255, 216, 255 }) })
    {
        using var artwork = await client.GetAsync(route);
        var bytes = await artwork.Content.ReadAsByteArrayAsync();
        Check(artwork.IsSuccessStatusCode && artwork.Content.Headers.ContentType?.MediaType == type && bytes.Length > signature.Length && bytes.AsSpan().StartsWith(signature), "overview artwork is embedded and served with its native format: " + route);
    }
    Check((await client.GetAsync("api/chapters")).StatusCode == HttpStatusCode.Unauthorized, "chapter status requires login");
    Check((await client.GetAsync("api/chapters/batch")).StatusCode == HttpStatusCode.Unauthorized, "chapter progress requires login");
    Check((await client.PostAsJsonAsync("api/chapters/batch/scan", new { })).StatusCode == HttpStatusCode.Unauthorized, "chapter scan requires login");
    Check((await client.PostAsJsonAsync("api/chapters/batch/fill", new {planId="fake"})).StatusCode == HttpStatusCode.Unauthorized, "chapter mutations require login");
    Check((await client.PostAsJsonAsync("api/library-language/scan", new { })).StatusCode == HttpStatusCode.Unauthorized, "library scan requires login");
    Check((await client.GetAsync("api/library-tasks")).StatusCode == HttpStatusCode.Unauthorized && (await client.PostAsJsonAsync("api/library-tasks/fill", new { })).StatusCode == HttpStatusCode.Unauthorized, "combined library tasks require login");
    Check((await client.PostAsJsonAsync("api/infuse/multiclip", new { enabled = true })).StatusCode == HttpStatusCode.Unauthorized, "multi-clip toggle requires login");
    Check((await client.PostAsJsonAsync("api/logs/clear", new { })).StatusCode == HttpStatusCode.Unauthorized, "clearing logs requires login");
    Check((await client.PostAsJsonAsync("api/probe/discover", new { embyUrl = options.embyUrl, embyApiKey = "test-secret" })).StatusCode == HttpStatusCode.Unauthorized, "discovery requires management login");
    var accountPath = Path.Combine(directory, "account.json");
    var account = JsonDocument.Parse(await File.ReadAllTextAsync(accountPath)).RootElement;
    Check(account.GetProperty("username").GetString() == "admin" && account.GetProperty("password").GetString() == "123456", "default account created alongside existing settings");
    Check(await File.ReadAllTextAsync(Path.Combine(directory, "management-key.txt")) == "legacy-management-key-preserved", "legacy key file preserved during upgrade");
    Check((await client.PostAsJsonAsync("api/login", new { username = "admin", password = "123456" })).StatusCode == HttpStatusCode.Forbidden, "cross-origin form mutation blocked");
    client.DefaultRequestHeaders.Add("X-Helper-Request", "1");
    Check((await client.PostAsJsonAsync("api/login", new { username = "wrong", password = "123456" })).StatusCode == HttpStatusCode.Unauthorized, "incorrect username rejected");
    Check((await client.PostAsJsonAsync("api/login", new { username = "admin", password = "wrong" })).StatusCode == HttpStatusCode.Unauthorized, "incorrect password rejected");
    Check((await client.PostAsJsonAsync("api/login", new { username = "admin", password = "123456" })).IsSuccessStatusCode, "default username and password login succeeds");
    var settingsResponse = await client.GetStringAsync("api/settings");
    Check(settingsResponse.Contains("test-secret"), "saved Emby API key returned to authenticated console");
    var isoReady = JsonDocument.Parse(await client.GetStringAsync("api/iso/status")).RootElement;
    Check(isoReady.GetProperty("canProbe").GetBoolean() && isoReady.GetProperty("pluginVersion").GetString()=="0.1.1" && isoReady.GetProperty("probeState").GetString()=="ready", "ISO endpoint checks loaded plugin and executes native protocol query");
    mockPluginLoaded=false;
    var isoMissing = JsonDocument.Parse(await client.GetStringAsync("api/iso/status")).RootElement;
    Check(!isoMissing.GetProperty("canProbe").GetBoolean() && isoMissing.GetProperty("pluginState").GetString()=="not-loaded", "unloaded plugin produces an actionable blocked status");
    mockPluginLoaded=true;
    Check(!JsonDocument.Parse(settingsResponse).RootElement.GetProperty("settings").GetProperty("enableInfuseMultiClipPlayback").GetBoolean(), "old settings default the new multi-clip feature to disabled");
    var multiClipWrites = mockWrites;
    var multiClip = await client.PostAsJsonAsync("api/infuse/multiclip", new { enabled = true });
    var multiClipResult = JsonDocument.Parse(await multiClip.Content.ReadAsStringAsync()).RootElement;
    Check(multiClip.IsSuccessStatusCode && multiClipResult.GetProperty("enabled").GetBoolean() && multiClipResult.GetProperty("readerAvailable").GetBoolean() && multiClipResult.GetProperty("reader").GetString() == "built-in", "multi-clip built-in reader is available without native tools");
    var multiClipSettings = JsonDocument.Parse(await client.GetStringAsync("api/settings")).RootElement.GetProperty("settings");
    Check(multiClipSettings.GetProperty("enableInfuseMultiClipPlayback").GetBoolean() && multiClipSettings.GetProperty("enableInfuseLanguagePatch").GetBoolean() && mockWrites == multiClipWrites, "multi-clip switch preserves language patch and never changes Emby probe options");
    await client.PostAsJsonAsync("api/infuse/multiclip", new { enabled = false });
    Check(!JsonDocument.Parse(await client.GetStringAsync("api/infuse/multiclip")).RootElement.GetProperty("enabled").GetBoolean(), "multi-clip feature can be disabled independently");
    Check(!(await client.GetStringAsync("api/account")).Contains("password"), "account read returns username without password");
    Check((await client.PostAsJsonAsync("api/settings", options with { embyApiKey = "replacement-token" })).IsSuccessStatusCode && (await client.GetStringAsync("api/settings")).Contains("replacement-token"), "API key edits persist and echo");
    Check((await client.PostAsJsonAsync("api/settings", options with { embyApiKey = "" })).IsSuccessStatusCode && !(await client.GetStringAsync("api/settings")).Contains("replacement-token"), "empty API key removes saved value");
    await client.PostAsJsonAsync("api/settings", options);
    var invalid = JsonSerializer.SerializeToNode(options)!; invalid["embyUrl"] = options.proxyUrl;
    Check((await client.PostAsJsonAsync("api/settings", invalid)).StatusCode == HttpStatusCode.BadRequest, "proxy recursion rejected");
    var emby = await client.PostAsJsonAsync("api/emby/test", new { });
    var embyResult = JsonDocument.Parse(await emby.Content.ReadAsStringAsync()).RootElement;
    Check(emby.IsSuccessStatusCode && embyResult.GetProperty("name").GetString() == "Test Emby", "Emby editor envelope and connection read correctly");
    Check(embyResult.GetProperty("originalProbePath").GetString() == Environment.ProcessPath && embyResult.GetProperty("originalProbeAccessible").GetBoolean(), "successful connection reports actual original probe for automatic filling");
    async Task<JsonElement> Discover() => JsonDocument.Parse(await (await client.PostAsJsonAsync("api/probe/discover", new { embyUrl = options.embyUrl, embyApiKey = "test-secret" })).Content.ReadAsStringAsync()).RootElement;
    Check((await Discover()).GetProperty("path").GetString() == Environment.ProcessPath && (await Discover()).GetProperty("accessible").GetBoolean(), "API discovery finds an accessible original file");
    mockProbeEnabled = true; mockProbePath = executable;
    Check((await Discover()).GetProperty("path").GetString() == Environment.ProcessPath, "API discovery never mistakes helper CustomProbePath for original ffprobe");
    foreach (var reference in new[] { @"C:\Users\owner\AppData\Roaming\Emby-Server\system\ffprobe.exe", "/var/packages/EmbyServer/target/bin/ffprobe", "/bin/ffprobe", "/opt/emby-server/bin/ffprobe", "/Applications/EmbyServer/ffprobe" })
    {
        mockOriginalProbePath = reference;
        Check((await Discover()).GetProperty("path").GetString() == reference, "Emby-provided path is discovered for platform: " + reference);
    }
    mockOriginalProbePath = Path.Combine(directory, "remote-container", "ffprobe");
    Check(!(await Discover()).GetProperty("accessible").GetBoolean(), "remote or container path is reported as inaccessible rather than runnable");
    mockOriginalProbePath = null;
    Check((await Discover()).GetProperty("path").ValueKind == JsonValueKind.Null, "older Emby without OriginalProbePath never falls back to active helper path");
    mockOriginalProbePath = executable;
    Check((await Discover()).GetProperty("path").ValueKind == JsonValueKind.Null, "self-referential original path is rejected");
    Check((await client.PostAsJsonAsync("api/probe/discover", new { embyUrl = options.embyUrl, embyApiKey = "" })).StatusCode == HttpStatusCode.BadRequest, "discovery requires supplied API key");
    Check((await client.PostAsJsonAsync("api/probe/discover", new { embyUrl = options.embyUrl, embyApiKey = "invalid-key" })).StatusCode == HttpStatusCode.BadRequest, "Emby authentication failure produces an actionable discovery error");
    Check((await client.PostAsJsonAsync("api/probe/discover", new { embyUrl = options.proxyUrl, embyApiKey = "test-secret" })).StatusCode == HttpStatusCode.BadRequest, "discovery cannot call helper media listener recursively");
    Check((await client.GetStringAsync("api/settings")).Contains("test-secret"), "discovery using supplied credentials does not modify saved settings");
    mockOriginalProbePath = Environment.ProcessPath; mockProbeEnabled = false; mockProbePath = "original";
    async Task<JsonElement> ProbeStatus() => JsonDocument.Parse(await client.GetStringAsync("api/probe/status")).RootElement;
    var probe = await ProbeStatus();
    Check(probe.GetProperty("state").GetString() == "disconnected" && probe.GetProperty("canApply").GetBoolean() && !probe.GetProperty("canRestore").GetBoolean(), "unconnected state offers apply only");
    mockProbeEnabled = true; mockProbePath = executable;
    probe = await ProbeStatus();
    Check(probe.GetProperty("state").GetString() == "connected" && !probe.GetProperty("canApply").GetBoolean() && !probe.GetProperty("canRestore").GetBoolean(), "own probe path identifies integration without assuming backup exists");
    var backupPath = Path.Combine(directory, "emby-probe-backup.json");
    await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(new { EmbyUrl = options.embyUrl, UseCustomProbePath = false, CustomProbePath = "original" }));
    Check((await ProbeStatus()).GetProperty("canRestore").GetBoolean(), "matching backup permits restore for connected probe");
    await client.PostAsJsonAsync("api/settings", options with { enableProbeLanguagePatch = false });
    probe = await ProbeStatus();
    Check(probe.GetProperty("state").GetString() == "connected" && !probe.GetProperty("languagePatchEnabled").GetBoolean(), "paused patch remains connected");
    if (OperatingSystem.IsWindows())
    {
        mockProbePath = executable.ToUpperInvariant();
        Check((await ProbeStatus()).GetProperty("state").GetString() == "connected", "Windows probe paths ignore case");
    }
    else
    {
        var alias = Path.Combine(directory, "dsm-target-alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(executable)!);
        try
        {
            mockProbePath = Path.Combine(alias, Path.GetFileName(executable));
            Check((await ProbeStatus()).GetProperty("state").GetString() == "connected", "DSM directory symlink aliases identify the same probe");
        }
        finally { Directory.Delete(alias); }
    }
    mockProbePath = Path.Combine(Path.GetDirectoryName(executable)!, "other-probe");
    probe = await ProbeStatus();
    Check(probe.GetProperty("state").GetString() == "other" && probe.GetProperty("canApply").GetBoolean() && !probe.GetProperty("canRestore").GetBoolean(), "another custom probe is not reported as integrated");
    mockProbePath = executable; mockProbeEnabled = false;
    Check((await ProbeStatus()).GetProperty("state").GetString() == "disconnected", "disabled Emby custom probe is not reported connected despite owned path");
    mockProbeEnabled = true;
    await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(new { EmbyUrl = "http://different-server:8096", UseCustomProbePath = false, CustomProbePath = "original" }));
    Check(!(await ProbeStatus()).GetProperty("canRestore").GetBoolean(), "backup for another Emby cannot be restored");
    await File.WriteAllTextAsync(backupPath, "invalid-json");
    Check((await ProbeStatus()).GetProperty("state").GetString() == "connected" && !(await ProbeStatus()).GetProperty("canRestore").GetBoolean(), "unreadable backup does not hide actual integration state");
    File.Delete(backupPath);
    await client.PostAsJsonAsync("api/settings", options with { embyApiKey = "" });
    probe = await ProbeStatus();
    Check(probe.GetProperty("state").GetString() == "unknown" && !probe.GetProperty("canApply").GetBoolean() && !probe.GetProperty("canRestore").GetBoolean(), "no API key means unknown rather than disconnected");
    await client.PostAsJsonAsync("api/settings", options with { embyApiKey = "invalid" });
    Check((await ProbeStatus()).GetProperty("state").GetString() == "unknown", "Emby authorization failure means unknown");
    await client.PostAsJsonAsync("api/settings", options);
    mockFailure = true;
    Check((await ProbeStatus()).GetProperty("state").GetString() == "unknown", "Emby unavailable means unknown instead of stale connected");
    mockFailure = false; mockProbeEnabled = false; mockProbePath = "original";
    Check((await ProbeStatus()).GetProperty("state").GetString() == "disconnected", "refresh recovers current state after connection failure");
    var toggle = await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true });
    Check(toggle.IsSuccessStatusCode && mockProbeEnabled && mockProbePath == executable, "turning on automatically selects helper probe");
    Check((await ProbeStatus()).GetProperty("languagePatchEnabled").GetBoolean() && File.Exists(backupPath), "turning on saves enabled flag and backs up original probe");
    var writes = mockWrites;
    Check((await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true })).IsSuccessStatusCode && mockWrites == writes, "enabling an already active helper does not rewrite Emby options");
    Check((await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false })).IsSuccessStatusCode && !mockProbeEnabled && mockProbePath == "original", "turning off restores original Emby options");
    Check(!(await ProbeStatus()).GetProperty("languagePatchEnabled").GetBoolean(), "turning off persists disabled patch flag");
    mockRejectMutation = true;
    Check(!(await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true })).IsSuccessStatusCode && !mockProbeEnabled && !(await ProbeStatus()).GetProperty("languagePatchEnabled").GetBoolean(), "failed enable leaves original probe and saved flag unchanged");
    mockRejectMutation = false; mockIgnoreMutation = true;
    Check(!(await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true })).IsSuccessStatusCode && !(await ProbeStatus()).GetProperty("languagePatchEnabled").GetBoolean(), "Emby must confirm the new probe before enabled flag is committed");
    mockIgnoreMutation = false;
    await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true });
    mockRejectMutation = true;
    Check(!(await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false })).IsSuccessStatusCode && mockProbePath == executable && (await ProbeStatus()).GetProperty("languagePatchEnabled").GetBoolean(), "failed disable keeps actual connected state and enabled flag");
    mockRejectMutation = false;
    File.Delete(backupPath);
    Check(!(await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false })).IsSuccessStatusCode && mockProbePath == executable, "missing backup prevents guessing the original probe on disable");
    await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(new { EmbyUrl = options.embyUrl, UseCustomProbePath = true, CustomProbePath = executable }));
    Check(!(await ProbeStatus()).GetProperty("canRestore").GetBoolean() && !(await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false })).IsSuccessStatusCode && mockProbePath == executable,
        "backup pointing to the active helper cannot falsely claim successful restoration");
    File.Delete(backupPath);
    mockProbeEnabled = true; mockProbePath = "third-party-probe";
    writes = mockWrites;
    Check((await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false })).IsSuccessStatusCode && mockWrites == writes && mockProbePath == "third-party-probe", "turning off never overwrites another custom probe");
    await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true });
    await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false });
    Check(mockProbeEnabled && mockProbePath == "third-party-probe", "on and off restore a previously enabled third-party custom probe");
    mockProbePath = "changed-after-last-disable";
    await client.PostAsJsonAsync("api/probe/toggle", new { enabled = true });
    await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false });
    Check(mockProbePath == "changed-after-last-disable", "each new enable backs up the current probe rather than restoring an obsolete backup");
    File.Delete(backupPath);
    mockProbeEnabled = false; mockProbePath = "original";
    Check((await client.PostAsJsonAsync("api/media-features", new { enabled = true, probePath = "missing" })).StatusCode == HttpStatusCode.BadRequest && !mockFeatures,
        "invalid supplementary engine cannot activate automatic enrichment");
    Check((await client.PostAsJsonAsync("api/media-features", new { enabled = true, probePath = Environment.ProcessPath })).StatusCode == HttpStatusCode.BadRequest,
        "supplementary probe must be separate from the original engine");
    var featureResponse = await client.PostAsJsonAsync("api/media-features", new { enabled = true, probePath = featureProbe, originalTrackTitles = true });
    Check(featureResponse.IsSuccessStatusCode && mockFeatures && mockTrackTitles && mockProbeEnabled && mockProbePath == executable,
        "supplementary features activate the scan plugin, attach the wrapper, and acknowledge the native title option");
    Check(!(await ProbeStatus()).GetProperty("languagePatchEnabled").GetBoolean(), "supplementary feature activation preserves the independent language switch");
    Check(featureRequestsBuffered, "Emby feature requests use PascalCase and buffered Content-Length instead of chunked JSON");
    Check(!(await client.PostAsJsonAsync("api/probe/toggle", new { enabled = false })).IsSuccessStatusCode && mockProbeEnabled,
        "restoring the probe is blocked while supplementary scanning needs the wrapper");
    await client.PostAsJsonAsync("api/settings", options);
    var featureStatus = JsonDocument.Parse(await client.GetStringAsync("api/media-features")).RootElement;
    Check(featureStatus.GetProperty("enabled").GetBoolean() && featureStatus.GetProperty("probePath").GetString() == featureProbe,
        "unrelated settings saves preserve supplementary configuration");
    mockProbeEnabled = false;
    featureStatus = JsonDocument.Parse(await client.GetStringAsync("api/media-features")).RootElement;
    Check(!featureStatus.GetProperty("probeConnected").GetBoolean() && featureStatus.GetProperty("error").GetString()!.Contains("接入"),
        "external disconnection is shown instead of reporting enabled scanning as ready");
    mockProbeEnabled = true;
    Check((await client.PostAsJsonAsync("api/media-features", new { enabled = false, probePath = featureProbe })).IsSuccessStatusCode && !mockFeatures && mockTrackTitles,
        "disabling automatic features preserves the native title setting when unspecified");
    mockIgnoreFeatures = true;
    Check(!(await client.PostAsJsonAsync("api/media-features", new { enabled = true, probePath = featureProbe })).IsSuccessStatusCode &&
        !JsonDocument.Parse(await client.GetStringAsync("api/media-features")).RootElement.GetProperty("enabled").GetBoolean(),
        "unacknowledged plugin activation cannot persist an enabled helper flag");
    mockIgnoreFeatures = false;
    mockPluginLoaded = false;
    featureStatus = JsonDocument.Parse(await client.GetStringAsync("api/media-features")).RootElement;
    Check(featureStatus.GetProperty("automatic").ValueKind == JsonValueKind.Null && featureStatus.GetProperty("error").GetString()!.Contains("0.1.3"),
        "missing feature plugin is reported with the required version");
    mockPluginLoaded = true;
    await client.PostAsJsonAsync("api/settings", options);
    using var media = new HttpClient();
    Check(await media.GetStringAsync(options.proxyUrl + "/sample") == "passthrough", "nonmatching requests forwarded byte-for-byte");
    Check((await media.GetAsync(options.proxyUrl + "/api/settings")).Content.Headers.ContentType?.MediaType == "text/plain", "management routes isolated from media listener");
    Check((await client.GetStringAsync("api/status")).Contains("requests"), "runtime statistics available");
    Check((await client.GetStringAsync("api/logs")).Contains("Settings"), "configuration events recorded");

    Check((await client.PostAsJsonAsync("api/account", new { username = " ", password = "valid" })).StatusCode == HttpStatusCode.BadRequest, "empty username rejected");
    Check((await client.PostAsJsonAsync("api/account", new { username = "valid", password = "" })).StatusCode == HttpStatusCode.BadRequest, "empty password rejected");
    using var staleClient = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = client.BaseAddress };
    staleClient.DefaultRequestHeaders.Add("Cookie", cookies.GetCookieHeader(client.BaseAddress!));
    const string newPassword = "new plaintext 密码 <>&";
    Check((await client.PostAsJsonAsync("api/account", new { username = "owner-test", password = newPassword })).IsSuccessStatusCode, "username and password can be changed");
    Check((await client.GetAsync("api/settings")).StatusCode == HttpStatusCode.Unauthorized, "changing account signs out current browser");
    Check((await staleClient.GetAsync("api/settings")).StatusCode == HttpStatusCode.Unauthorized, "changing account invalidates other sessions");
    account = JsonDocument.Parse(await File.ReadAllTextAsync(accountPath)).RootElement;
    Check(account.GetProperty("username").GetString() == "owner-test" && account.GetProperty("password").GetString() == newPassword, "changed credentials stored as plaintext JSON");
    Check((await client.PostAsJsonAsync("api/login", new { username = "admin", password = "123456" })).StatusCode == HttpStatusCode.Unauthorized, "old credentials stop working");
    Check((await client.PostAsJsonAsync("api/login", new { username = "owner-test", password = newPassword })).IsSuccessStatusCode, "changed credentials work immediately");
    await client.PostAsJsonAsync("api/settings", options);
    Check((await client.GetStringAsync("api/account")).Contains("owner-test"), "saving general settings preserves account");
    staleClient.DefaultRequestHeaders.Remove("Cookie");
    staleClient.DefaultRequestHeaders.Add("Cookie", cookies.GetCookieHeader(client.BaseAddress!));
    Check((await client.PostAsJsonAsync("api/account", new { username = "owner-test", password = "password-only-change" })).IsSuccessStatusCode, "password can change without changing username");
    Check((await staleClient.GetAsync("api/settings")).StatusCode == HttpStatusCode.Unauthorized, "password-only change invalidates previous sessions");
    Check((await client.PostAsJsonAsync("api/login", new { username = "owner-test", password = "password-only-change" })).IsSuccessStatusCode, "new password works for unchanged username");
    Check(!(await client.GetStringAsync("api/logs")).Contains("password-only-change"), "account passwords absent from logs");

    var logsDirectory = Path.Combine(directory, "logs");
    var archive = Path.Combine(logsDirectory, "helper-20000101.jsonl.previous");
    var history = Path.Combine(logsDirectory, "helper-20000102.jsonl");
    var unrelated = Path.Combine(logsDirectory, "helper-not-a-log.txt");
    var nested = Path.Combine(logsDirectory, "keep");
    Directory.CreateDirectory(nested);
    await File.WriteAllTextAsync(archive, "historical-log-fixture");
    await File.WriteAllTextAsync(history, "historical-log-fixture");
    await File.WriteAllTextAsync(unrelated, "unrelated-file");
    await File.WriteAllTextAsync(Path.Combine(nested, "helper-20000101.jsonl"), "nested-file");
    var settingsBeforeClear = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
    var accountBeforeClear = await File.ReadAllTextAsync(accountPath);
    var statusBeforeClear = JsonDocument.Parse(await client.GetStringAsync("api/status")).RootElement;
    using var noCsrf = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = client.BaseAddress };
    noCsrf.DefaultRequestHeaders.Add("Cookie", cookies.GetCookieHeader(client.BaseAddress!));
    Check((await noCsrf.PostAsJsonAsync("api/library-language/scan", new { })).StatusCode == HttpStatusCode.Forbidden, "library task changes require the management request header");
    Check((await noCsrf.PostAsJsonAsync("api/library-tasks/fill", new { libraryIds = new[] { "a" }, steps = new[] { new { task = "language", planId = "x" } } })).StatusCode == HttpStatusCode.Forbidden, "combined fill requires the management request header");
    var pipelineIdle = JsonDocument.Parse(await client.GetStringAsync("api/library-tasks")).RootElement;
    Check(pipelineIdle.GetProperty("status").GetString() == "idle" && !pipelineIdle.GetProperty("active").GetBoolean(), "combined task starts idle");
    using (var noSteps = await client.PostAsJsonAsync("api/library-tasks/fill", new { libraryIds = new[] { "a" }, steps = Array.Empty<object>() }))
        Check(noSteps.StatusCode == HttpStatusCode.BadRequest && (await noSteps.Content.ReadAsStringAsync()).Contains("补全内容"), "combined fill needs at least one task");
    using (var noLibrary = await client.PostAsJsonAsync("api/library-tasks/fill", new { libraryIds = Array.Empty<string>(), steps = new[] { new { task = "language", planId = "x" } } }))
        Check(noLibrary.StatusCode == HttpStatusCode.BadRequest && (await noLibrary.Content.ReadAsStringAsync()).Contains("媒体库"), "combined fill needs a library");
    Check((await noCsrf.PostAsJsonAsync("api/media-features/batch/fill", new { planId = "none", libraryIds = new[] { "a" } })).StatusCode == HttpStatusCode.Forbidden, "feature batch changes require the management request header");
    Check((await noCsrf.PostAsJsonAsync("api/logs/clear", new { })).StatusCode == HttpStatusCode.Forbidden && File.Exists(history), "clearing logs requires the management request header");
    if (OperatingSystem.IsWindows())
    {
        using (var locked = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check((await client.PostAsJsonAsync("api/logs/clear", new { })).StatusCode == HttpStatusCode.BadRequest, "a locked log file reports clear failure instead of false success");
    }
    var clear = await client.PostAsJsonAsync("api/logs/clear", new { });
    Check(clear.IsSuccessStatusCode, "authenticated clear deletes all runtime logs");
    Check(JsonDocument.Parse(await client.GetStringAsync("api/logs")).RootElement.GetArrayLength() == 0, "clearing logs empties in-memory and persisted entries");
    Check(!File.Exists(history) && !File.Exists(archive), "clear removes historical and rotated log files");
    Check(await File.ReadAllTextAsync(unrelated) == "unrelated-file" && File.Exists(Path.Combine(nested, "helper-20000101.jsonl")), "clear is limited to helper-owned log files in the log directory");
    Check(await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")) == settingsBeforeClear && await File.ReadAllTextAsync(accountPath) == accountBeforeClear, "clearing logs preserves configuration and account files");
    Check((await client.GetStringAsync("api/logs/download")).Length == 0, "log download is empty immediately after clearing");
    var statusAfterClear = JsonDocument.Parse(await client.GetStringAsync("api/status")).RootElement;
    Check(statusAfterClear.GetProperty("requests").GetInt64() == statusBeforeClear.GetProperty("requests").GetInt64() &&
        statusAfterClear.GetProperty("started").GetString() == statusBeforeClear.GetProperty("started").GetString(), "clearing logs leaves request statistics and uptime unchanged");
    Check((await client.PostAsJsonAsync("api/logs/clear", new { })).IsSuccessStatusCode, "clearing an empty log history succeeds");
    await client.PostAsJsonAsync("api/settings", options);
    Check((await client.GetStringAsync("api/logs")).Contains("Settings"), "new logs are recorded after clearing");
    await client.PostAsJsonAsync("api/logs/clear", new { });

    await client.PostAsJsonAsync("api/infuse/multiclip", new { enabled = true });
    process.Kill(true); await process.WaitForExitAsync(); await stdout; await stderr;
    process.Start(); stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
    for (var retry = 0; ; retry++)
    {
        try { if ((await client.GetAsync("/")).IsSuccessStatusCode) break; }
        catch (HttpRequestException) when (retry < 100) { }
        if (retry >= 100 || process.HasExited) throw new Exception("Helper did not restart: " + await stderr);
        await Task.Delay(100);
    }
    Check((await client.GetAsync("api/settings")).IsSuccessStatusCode, "current session survives normal helper restart");
    Check(JsonDocument.Parse(await client.GetStringAsync("api/infuse/multiclip")).RootElement.GetProperty("enabled").GetBoolean(), "multi-clip independent setting persists after restart");
    Check(!(await client.GetStringAsync("api/logs")).Contains("Settings"), "cleared historical records do not reappear after restart");
    await client.PostAsJsonAsync("api/logout", new { });
    Check((await client.GetAsync("api/settings")).StatusCode == HttpStatusCode.Unauthorized, "logout invalidates session");
    Check((await client.PostAsJsonAsync("api/login", new { username = "admin", password = "123456" })).StatusCode == HttpStatusCode.Unauthorized, "restart does not reset changed account to defaults");
    Check((await client.PostAsJsonAsync("api/login", new { username = "owner-test", password = "password-only-change" })).IsSuccessStatusCode, "changed account persists across restart");
    Check((await client.GetStringAsync("api/settings")).Contains("test-secret"), "Emby API key persists across restart");
    Console.WriteLine($"Passed {count} application checks.");
}
finally
{
    if (!process.HasExited) process.Kill(true);
    await process.WaitForExitAsync(); cancellation.Cancel(); backend.Stop();
    await backendTask;
    File.Delete(featureProbe);
    Directory.Delete(directory, true);
}
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
static int Port() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
