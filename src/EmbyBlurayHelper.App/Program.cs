using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using EmbyBlurayHelper.App;
using EmbyBlurayHelper.ProbeWrapper;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.ProbeWrapper.MediaFeatures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

if (args.Length == 1 && args[0] == "--helper-version")
{
    Console.WriteLine("EmbyBlurayHelper 0.2.0");
    return 0;
}

var serve = args.Length == 0 || args[0] == "--serve";
if (args.Length == 4 && args[0] == "--helper-thumbnail-session")
{
    var output = Console.Out;
    try
    {
        Console.SetOut(Console.Error);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromHours(6));
        await using var session = await EmbyBlurayHelper.ProbeWrapper.Thumbnails.DiscThumbnailSession.Open(args[1],
            long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), lifetime.Token);
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(session.Description));
        await output.FlushAsync();
        await Console.In.ReadLineAsync(lifetime.Token);
        return 0;
    }
    catch (Exception e) { await Console.Error.WriteLineAsync(HelperLog.Redact(e.Message)); return 1; }
    finally { Console.SetOut(output); }
}
if (args.Length >= 2 && args[0] == "--helper-chapters")
{
    var output = Console.Out;
    try
    {
        Console.SetOut(Console.Error); // BDInfo diagnostics must never corrupt the JSON protocol.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var runtime = args.Length > 2 ? long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var size = args.Length > 3 ? long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var chapterData = DiscChapterReader.Read(args[1], null, timeout.Token, runtime, size);
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(chapterData));
        return 0;
    }
    catch (Exception e) { await Console.Error.WriteLineAsync(HelperLog.Redact(e.Message)); return 1; }
    finally { Console.SetOut(output); }
}
var dataDirectory = Environment.GetEnvironmentVariable("EMBY_BLURAY_HELPER_DATA");
if (serve && Array.IndexOf(args, "--data-dir") is var index && index >= 0 && index + 1 < args.Length) dataDirectory = args[index + 1];
var maintenance = args.Length == 1 && args[0] == "--prepare-uninstall";
if (!serve && !maintenance)
{
    HelperLog? probeLog = null;
    try
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        ProbeSettings probeSettings;
        if (File.Exists(ProbeSettings.SharedPath)) probeSettings = ProbeSettings.ReadShared();
        else
        {
            var probeStore = new SettingsStore(dataDirectory);
            probeLog = new HelperLog(probeStore.DataDirectory);
            probeSettings = new ProbeSettings(probeStore.Current.UpstreamProbePath, probeStore.Current.EnableProbeLanguagePatch,
                probeStore.Current.EnableMediaFeatures, probeStore.Current.FeatureProbePath);
        }
        var upstream = probeSettings.UpstreamProbePath;
        if (string.IsNullOrWhiteSpace(upstream)) upstream = await ProbeConfiguration.ResolveUpstreamPathAsync(cancel.Token);
        if (Path.GetFullPath(upstream) == Environment.ProcessPath || Path.GetFullPath(upstream) == ProbeSettings.ExecutablePath) throw new InvalidOperationException("原版 ffprobe 路径指向了本工具。");
        var probeArgs = args[0] == "--check-upstream" ? new[] { "-version" } : args[0] == "--probe" ? args[1..] : args;
        var features = new MediaFeatureOptions(probeSettings.EnableMediaFeatures, probeSettings.FeatureProbePath);
        if (features.Enabled && !string.IsNullOrWhiteSpace(features.ProbePath) &&
            string.Equals(Path.GetFullPath(upstream), Path.GetFullPath(features.ProbePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            features = features with { Enabled = false };
        var exit = await ProbeProcess.RunAsync(upstream, probeArgs, cancel.Token, probeSettings.EnableProbeLanguagePatch, features);
        probeLog?.Add(exit == 0 ? "Information" : "Warning", "Probe", $"媒体探测完成，退出码 {exit}，语言补全{(probeSettings.EnableProbeLanguagePatch ? "已启用" : "已关闭")}。");
        return exit;
    }
    catch (OperationCanceledException) { return 130; }
    catch (Exception exception)
    {
        probeLog?.Add("Error", "Probe", "探测失败：" + exception.GetType().Name);
        await Console.Error.WriteLineAsync("EmbyBlurayHelper: " + HelperLog.Redact(exception.Message));
        return 1;
    }
}

var settings = new SettingsStore(dataDirectory);
var log = new HelperLog(settings.DataDirectory);
if (maintenance)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        await new EmbyIntegration(settings, client, log).PrepareUninstall(CancellationToken.None);
        return 0;
    }
    catch (Exception exception)
    {
        await Console.Error.WriteLineAsync("请先恢复 Emby 原探测器再卸载：" + HelperLog.Redact(exception.Message));
        return 1;
    }
}

var accounts = new AccountStore(settings.DataDirectory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls(settings.Startup.ManagementUrl, settings.Startup.ProxyUrl);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(log);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "EmbyBlurayHelper.Admin";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        if (context.Principal?.FindFirst("account_stamp")?.Value != AccountStore.SessionStamp(accounts.Current))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) })));
builder.Services.AddMemoryCache(options => options.SizeLimit = 128);
using var mediaClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
using var apiClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var integration = new EmbyIntegration(settings, apiClient, log);
using var libraryClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var mediaFeatures = new MediaFeatureService(settings, integration, libraryClient, log);
await using var featureBatch = new MediaFeatureBatch(settings, libraryClient, log, mediaFeatures.ProbeSource);
await using var libraryTask = new LibraryLanguageTask(settings, libraryClient, log);
var chapters = new NativeChapterService(settings, libraryClient, log);
await using var chapterBatch = new NativeChapterBatch(settings, libraryClient, chapters, log);
var app = builder.Build();
app.UseWebSockets();
var proxies = new Dictionary<ProxyConfiguration, EmbyProxy>();
var proxyGate = new object();
// One persisted record shared by every proxy instance; it outlives settings changes.
var discListings = new EmbyBlurayHelper.Proxy.Chapters.DiscListingStore(Path.Combine(settings.DataDirectory, "infuse-disc-listings.json"), app.Services.GetRequiredService<ILogger<EmbyProxy>>());
var managementPort = new Uri(settings.Startup.ManagementUrl).Port;
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == managementPort) { await next(context); return; }
    log.BeginRequest();
    try
    {
        EmbyProxy proxy;
        var config = settings.ProxyConfiguration();
        lock (proxyGate)
        {
            if (!proxies.TryGetValue(config, out proxy!))
            {
                proxies.Clear();
                proxy = new EmbyProxy(config, mediaClient, app.Services.GetRequiredService<IMemoryCache>(), app.Services.GetRequiredService<ILogger<EmbyProxy>>(), discListings: discListings);
                proxies[config] = proxy;
            }
        }
        await proxy.HandleAsync(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) when (e is HttpRequestException or IOException)
    {
        log.Add("Warning", "Proxy", "上游连接中断：" + e.GetType().Name);
        if (!context.Response.HasStarted) context.Response.StatusCode = 502; else context.Abort();
    }
    finally { log.EndRequest(); }
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/login" && context.User.Identity?.IsAuthenticated != true)
    { context.Response.StatusCode = 401; return; }
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-Helper-Request"] != "1")
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
    {
        log.Add("Warning", "Management", "操作失败：" + e.GetType().Name);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = e is HttpRequestException ? "Emby 请求失败，请检查地址和 API key。" : e is OperationCanceledException ? "操作超时或已取消。" : HelperLog.Redact(e.Message) });
        }
    }
});

foreach (var (route, name, type) in new[] { ("/", "index.html", "text/html; charset=utf-8"), ("/app.css", "app.css", "text/css"), ("/batch.js", "batch.js", "text/javascript"), ("/app.js", "app.js", "text/javascript"), ("/media-features.js", "media-features.js", "text/javascript"), ("/chapters.js", "chapters.js", "text/javascript") })
    app.MapGet(route, () => Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbyBlurayHelper.App.Web." + name)!, type));
app.MapPost("/api/login", async (LoginInput input, HttpContext context) =>
{
    var account = accounts.Current;
    if (!AccountStore.Matches(account, input.Username, input.Password))
        return Results.Json(new { error = "用户名或密码不正确。" }, statusCode: 401);
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(ClaimTypes.Name, account.Username), new Claim("account_stamp", AccountStore.SessionStamp(account))
    ], CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { message = "登录成功" });
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.Ok(); });
app.MapGet("/api/account", () => new { username = accounts.Current.Username });
app.MapPost("/api/account", async (LoginInput input, HttpContext context) =>
{
    accounts.Save(input.Username, input.Password);
    await context.SignOutAsync();
    log.Add("Information", "Account", "管理账号已更新，请使用新账号重新登录。");
    return Results.Ok(new { message = "账号已保存，请重新登录。" });
});
app.MapGet("/api/settings", () => new { settings = settings.Current, hasApiKey = settings.Current.EmbyApiKey.Length > 0, restartRequired = settings.RestartRequired,
    executablePath = Environment.ProcessPath, dataDirectory = settings.DataDirectory, backupAvailable = File.Exists(Path.Combine(settings.DataDirectory, "emby-probe-backup.json")) });
app.MapPost("/api/settings", async (HelperSettings input, CancellationToken token) =>
{
    SettingsStore.Validate(input);
    await integration.SaveSettings(input, token);
    if (app.Services.GetRequiredService<IMemoryCache>() is MemoryCache cache) cache.Compact(1);
    log.Add("Information", "Settings", "配置已保存。功能开关与 Emby 地址立即生效；监听端口变更需重新启动工具。");
    return Results.Ok(new { restartRequired = settings.RestartRequired });
});
app.MapGet("/api/status", () => log.Status());
app.MapGet("/api/logs", () => log.Entries());
app.MapPost("/api/logs/clear", () => Results.Ok(new { deletedFiles = log.Clear(), message = "全部运行日志已清除。" }));
app.MapGet("/api/logs/download", () => Results.File(Encoding.UTF8.GetBytes(string.Join('\n', log.Entries().Select(e => $"{e.Time:O} [{e.Level}] {e.Source}: {e.Message}"))), "text/plain; charset=utf-8", "EmbyBlurayHelper.log"));
app.MapPost("/api/detect", () => EmbyIntegration.Detect());
app.MapPost("/api/emby/test", (CancellationToken token) => integration.Test(token));
app.MapGet("/api/probe/status", (CancellationToken token) => integration.GetProbeStatus(token));
app.MapGet("/api/infuse/multiclip", () => integration.GetMultiClipStatus());
// Read-only: from the persisted listing record; never reads media files.
app.MapGet("/api/infuse/chapters", () => { var (available, unavailable) = discListings.Summary();
    return new { enabled = settings.Current.EnableInfuseChapters, available, unavailable = unavailable.Select(u => new { itemId = u.ItemId, name = u.Name, reason = u.Reason }) }; });
app.MapGet("/api/infuse/chapters/{itemId}", (string itemId) => new { enabled = settings.Current.EnableInfuseChapters,
    sources = discListings.ForItem(itemId).Select(s => new { sourceId = s.SourceId, iso = s.Listing is not null, chapters = s.Listing?.ChapterCount, playlist = s.Listing?.Playlist, reason = s.Reason }) });
app.MapPost("/api/infuse/multiclip", (ProbeToggleInput input, CancellationToken token) => integration.SetMultiClipEnabled(input.Enabled, token));
app.MapPost("/api/probe/toggle", (ProbeToggleInput input, CancellationToken token) => integration.SetProbeEnabled(input.Enabled, token));
app.MapPost("/api/probe/check", (CancellationToken token) => integration.CheckProbe(token));
app.MapPost("/api/probe/discover", (ProbeDiscoveryInput input, CancellationToken token) => integration.DiscoverProbe(input.EmbyUrl, input.EmbyApiKey, token));
app.MapPost("/api/probe/apply", (CancellationToken token) => integration.ApplyProbe(false, token));
app.MapPost("/api/probe/restore", (CancellationToken token) => integration.ApplyProbe(true, token));
app.MapGet("/api/library-language", () => libraryTask.Snapshot());
app.MapGet("/api/iso/status", (CancellationToken token) => libraryTask.CheckIsoPrerequisites(token));
app.MapPost("/api/library-language/scan", () => Results.Json(libraryTask.Scan(), statusCode: 202));
app.MapPost("/api/library-language/fill", (LibraryPlanInput input) => Results.Json(libraryTask.Fill(input.PlanId, input.LibraryIds), statusCode: 202));
app.MapPost("/api/library-language/cancel", () => libraryTask.Cancel());
app.MapPost("/api/disc/check", (DiscInput input, HttpContext context) => integration.Inspect(input.ItemId, context.RequestAborted));
app.MapGet("/api/media-features", (CancellationToken token) => mediaFeatures.Status(token));
app.MapPost("/api/media-features", (MediaFeatureInput input, CancellationToken token) => mediaFeatures.Configure(input.Enabled, input.ProbePath, input.OriginalTrackTitles, token));
app.MapPost("/api/media-features/check", async (FeatureProbeInput input, CancellationToken token) => new { version = await MediaFeatureService.CheckEngine(input.ProbePath, token) });
app.MapPost("/api/media-features/probe", (DiscInput input, CancellationToken token) => mediaFeatures.Probe(input.ItemId, token));
app.MapGet("/api/media-features/batch", () => featureBatch.Snapshot());
app.MapPost("/api/media-features/batch/scan", () => Results.Json(featureBatch.Scan(), statusCode: 202));
app.MapPost("/api/media-features/batch/fill", (FeatureBatchInput input) => Results.Json(featureBatch.Fill(input.PlanId, input.LibraryIds, input.IncludeVerified), statusCode: 202));
app.MapPost("/api/media-features/batch/cancel", () => featureBatch.Cancel());
app.MapGet("/api/chapters", (CancellationToken token) => chapters.Status(token));
app.MapPost("/api/chapters", (ChapterSettingsInput input, CancellationToken token) => chapters.Configure(input.Enabled, input.ReaderPath, token));
app.MapGet("/api/chapters/batch", () => chapterBatch.Snapshot());
app.MapPost("/api/chapters/batch/scan", () => Results.Json(chapterBatch.Scan(), statusCode: 202));
app.MapPost("/api/chapters/batch/fill", (FeatureBatchInput input) => Results.Json(chapterBatch.Fill(input.PlanId, input.LibraryIds, input.IncludeVerified), statusCode: 202));
app.MapPost("/api/chapters/batch/cancel", () => chapterBatch.Cancel());
log.Add("Information", "Helper", "EmbyBlurayHelper 0.2.0 已启动。管理界面与播放代理独立监听。");
Console.WriteLine($"EmbyBlurayHelper 0.2.0\nManagement: {settings.Startup.ManagementUrl}\nProxy: {settings.Startup.ProxyUrl}\nAccount file: {accounts.FilePath}");
await app.RunAsync();
return 0;

internal sealed record LoginInput(string Username, string Password);
internal sealed record DiscInput(string ItemId);
internal sealed record ProbeToggleInput(bool Enabled);
internal sealed record ProbeDiscoveryInput(string EmbyUrl, string EmbyApiKey);
internal sealed record LibraryPlanInput(string PlanId, string[]? LibraryIds = null);
internal sealed record MediaFeatureInput(bool Enabled, string ProbePath, bool? OriginalTrackTitles = null);
internal sealed record FeatureProbeInput(string ProbePath);
internal sealed record FeatureBatchInput(string PlanId, string[]? LibraryIds = null, bool IncludeVerified = false);
internal sealed record ChapterSettingsInput(bool Enabled, string? ReaderPath = null);
