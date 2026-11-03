using System.Collections;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.Proxy.Chapters;
using Microsoft.Extensions.Caching.Memory;

// Local diagnostic executable only; never included in portable/SPK packages.
// It exercises either byte substitution for item 12, or only the production
// transport for real ISO 1886 with unchanged metadata. No persistent writes.
if (args.Length is < 3 or > 4 || args[1] != "12" || args[2] != "1886")
    throw new ArgumentException("Usage: InfuseIsolation <local-settings.json> 12 1886 [known-good-iso|real-iso-transport|native-iso-metadata|virtual-iso-native]");
var mode = args.Length == 4 ? args[3] : "known-good-iso";
if (mode is not "known-good-iso" and not "real-iso-transport" and not "native-iso-metadata" and not "virtual-iso-native")
    throw new ArgumentException("Unsupported diagnostic mode.");
var transportControl = mode == "real-iso-transport";
var virtualControl = mode == "virtual-iso-native";
var metadataControl = mode == "native-iso-metadata" || virtualControl;
var displayedItem = transportControl ? "1886" : "12";
using var settings = JsonDocument.Parse(File.ReadAllText(args[0]));
var saved = settings.RootElement;
var upstream = saved.GetProperty("embyUrl").GetString()!;
if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Port != 8096)
    throw new InvalidOperationException("Diagnostic is restricted to the local test Emby on 8096.");
var config = new ProxyConfiguration
{
    ListenUrl = "http://0.0.0.0:8097",
    UpstreamBaseUrl = upstream,
    EnableInfuseLanguagePatch = saved.GetProperty("enableInfuseLanguagePatch").GetBoolean(),
    EnableInfuseMultiClipPlayback = saved.GetProperty("enableInfuseMultiClipPlayback").GetBoolean(),
    EnableInfuseChapters = mode == "known-good-iso"
};
using var client = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None
}) { Timeout = Timeout.InfiniteTimeSpan };
using var lookup = new HttpRequestMessage(HttpMethod.Get, upstream.TrimEnd('/') + "/emby/Items?Ids=12,1886&Fields=Path,MediaSources,Chapters");
lookup.Headers.TryAddWithoutValidation("X-Emby-Token", saved.GetProperty("embyApiKey").GetString());
using var lookupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var lookupResponse = await client.SendAsync(lookup, lookupTimeout.Token);
lookupResponse.EnsureSuccessStatusCode();
using var item = JsonDocument.Parse(await lookupResponse.Content.ReadAsStringAsync(lookupTimeout.Token));
var donor = item.RootElement.GetProperty("Items").EnumerateArray().Single(i => i.GetProperty("Id").GetString() == "1886");
var originalIso = donor.GetProperty("Path").GetString()!;
var file = new FileInfo(originalIso);
if (!file.Exists || !file.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("The known-good item is not an available ISO file.");
var raw = RawImage(file);
using (var probe = raw.Open())
{
    probe.Position = 16 * 2048;
    var recognition = new byte[7]; probe.ReadExactly(recognition);
    if (Encoding.ASCII.GetString(recognition, 1, 5) != "BEA01")
        throw new InvalidDataException("Known-good ISO recognition sequence differs.");
}
var identity = $"known-good-iso/1886/{file.Length}/{file.LastWriteTimeUtc.Ticks}";
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.UseUrls(config.ListenUrl);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddMemoryCache(o => o.SizeLimit = 128);
var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<EmbyProxy>>();
DiscChapterPresentation? Substitute(JsonElement source)
{
    if (source.GetProperty("Id").GetString() != "mediasource_12") return null;
    var presentation = DiscChapterPresentation.Create(source);
    if (presentation is null) return null;
    if (virtualControl)
    {
        logger.LogInformation("VIRTUAL CONTROL: item 12 uses its own virtual UDF image; {Length} bytes; {Chapters} saved chapters; native-style ISO metadata.", presentation.Image.Length, presentation.ChapterCount);
        return presentation;
    }
    var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + presentation.Signature))).ToLowerInvariant();
    logger.LogInformation("ISOLATION: item 12 advertises known-good item 1886 ISO bytes; {Length} bytes; no virtual UDF generation or media modification in the served payload.", raw.Length);
    return presentation with { Image = raw, Signature = signature };
}
var nativeMetadata = new NativeIsoMetadata(donor, virtualControl ? Substitute : null);
var actualImage = virtualControl ? Substitute(item.RootElement.GetProperty("Items").EnumerateArray().Single(i => i.GetProperty("Id").GetString() == "12").GetProperty("MediaSources")[0])!.Image : raw;
var proxyCache = app.Services.GetRequiredService<IMemoryCache>();
var proxy = new EmbyProxy(config, client, proxyCache, logger, Substitute);
var substitutionProxy = new EmbyProxy(config with { EnableInfuseChapters = true }, client, proxyCache, logger, Substitute);
// Use a separate proxy/cache for stream requests only. Its presentation must
// never leak into metadata through Build's cache: every item/list/playback API
// response in this control is passed through with Emby's native ISO metadata.
using var transportCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 8 });
var transportSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("real-iso-transport/" + identity))).ToLowerInvariant();
var transportProxy = new EmbyProxy(config with { EnableInfuseChapters = true }, client, transportCache, logger, source =>
{
    if (source.GetProperty("Id").GetString() != "mediasource_1886" ||
        !string.Equals(source.GetProperty("Path").GetString(), file.FullName, StringComparison.OrdinalIgnoreCase)) return null;
    logger.LogInformation("CONTROL: real ISO item 1886 uses the production chapter HTTP transport; metadata and bytes remain original.");
    return new DiscChapterPresentation(raw, transportSignature, "original ISO", source.GetProperty("Chapters").GetArrayLength());
});
var controlStream = new Regex("^(?:/emby)?/videos/1886/stream(?:\\.[a-z0-9]+)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
var substitutionStream = new Regex("^(?:/emby)?/videos/12/stream(?:\\.[a-z0-9]+)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
app.UseWebSockets();
app.Run(async context =>
{
    if (context.Request.Path == "/__diagnostic/status")
    {
        await context.Response.WriteAsJsonAsync(new { mode, displayedItem, actualContent = virtualControl ? "12" : "1886", bytes = actualImage.Length,
            metadata = virtualControl ? "item 12 own tracks and chapters; native-style ISO fields" : transportControl ? "unchanged Emby metadata" : metadataControl ? "native item 1886 ISO source, item 12 identity" : "item 12 with ISO presentation" });
        return;
    }
    var isControlStream = transportControl && controlStream.IsMatch(context.Request.Path.Value ?? "");
    if (isControlStream || !transportControl && context.Request.Path.Value?.Contains("/Videos/12/stream", StringComparison.OrdinalIgnoreCase) == true)
        context.Response.OnStarting(() =>
        {
            var range = context.Request.Headers.Range.ToString();
            if (range.Length > 128 || !Regex.IsMatch(range, "^bytes=[0-9, -]+$")) range = range.Length == 0 ? "none" : "invalid";
            logger.LogInformation("ISOLATION: response headers item {ItemId}; {Method} {Path}; {Status}; {Type}; {Length} bytes; requested range {Range}.",
                displayedItem, context.Request.Method, context.Request.Path.Value, context.Response.StatusCode,
                context.Response.ContentType, context.Response.ContentLength, range);
            return Task.CompletedTask;
        });
    try
    {
        if (metadataControl && EmbyProxy.IsInfuse(context.Request) && NativeIsoMetadata.Route.IsMatch(context.Request.Path.Value ?? "") &&
            !HttpMethods.IsHead(context.Request.Method))
        {
            var output = context.Response.Body;
            var originalEncoding = context.Request.Headers.AcceptEncoding;
            using var buffer = new MemoryStream();
            byte[]? changed = null;
            try
            {
                context.Response.Body = buffer;
                context.Request.Headers.AcceptEncoding = "identity";
                await proxy.HandleAsync(context);
                if (context.Response.StatusCode is >= 200 and < 300 && context.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                    changed = nativeMetadata.Rewrite(context.Request.Path.Value!, buffer.ToArray());
            }
            finally
            {
                context.Response.Body = output;
                context.Request.Headers.AcceptEncoding = originalEncoding;
            }
            if (changed is not null)
            {
                foreach (var name in new[] { "ETag", "Content-Encoding", "Content-MD5" }) context.Response.Headers.Remove(name);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentLength = changed.Length;
                logger.LogInformation("METADATA CONTROL: item 12 uses {Presentation}; original item identity retained; route {Route}.",
                    virtualControl ? "its own tracks, runtime and virtual ISO; no injected DirectStreamUrl or MimeType" : "item 1886 native ISO source, tracks and duration", context.Request.Path.Value);
                await output.WriteAsync(changed, context.RequestAborted);
            }
            else
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(output, context.RequestAborted);
            }
            return;
        }
        var handler = isControlStream ? transportProxy : metadataControl && substitutionStream.IsMatch(context.Request.Path.Value ?? "") ? substitutionProxy : proxy;
        await handler.HandleAsync(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
logger.LogWarning("LOCAL DIAGNOSTIC on 8097: mode {Mode}; open item {ItemId}, expected video is {Title}. Main Helper management is temporarily offline. Media and configuration are unchanged.", mode, displayedItem, virtualControl ? "Minority Report" : "Shutter Island");
await app.RunAsync();

static VirtualUdfImage RawImage(FileInfo file)
{
    // Keep raw-file construction in this test harness, not in production's UDF
    // builder. Its single extent maps the existing ISO without copying it.
    var image = new VirtualUdfImage();
    typeof(VirtualUdfImage).GetProperty("Length", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(image, file.Length);
    var extentType = typeof(VirtualUdfImage).GetNestedType("Extent", BindingFlags.NonPublic)!;
    var extent = Activator.CreateInstance(extentType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null, new object?[] { 0L, file.Length, null, file.FullName }, null)!;
    var extents = (IList)typeof(VirtualUdfImage).GetField("extents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
    extents.Add(extent);
    return image;
}
