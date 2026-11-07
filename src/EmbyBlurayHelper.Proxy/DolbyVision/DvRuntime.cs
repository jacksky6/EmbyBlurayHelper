using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

internal sealed record DvOptions(bool Enabled, string Ffmpeg, string Library, int SessionMiB, int GlobalMiB, string DiscContainer = "iso")
{
    internal static DvOptions From(ProxyConfiguration config) => new(config.EnableInfuseDolbyVision, config.DolbyVisionFfmpegPath,
        config.DolbyVisionLibraryPath, config.DolbyVisionCacheMiB, config.DolbyVisionGlobalCacheMiB, config.InfuseDiscContainer);
    internal bool Available => File.Exists(Ffmpeg) && File.Exists(Library);
}

internal sealed class DvRuntime : IDisposable
{
    internal sealed class Presentation(DvDiscPlan plan)
    {
        internal DvDiscPlan Plan = plan;
        internal byte[]? Header;
        internal string? Tracks;
        internal readonly SemaphoreSlim HeaderGate = new(1);
        internal DateTime Access = DateTime.UtcNow;
        internal void Verify(DvFragment fragment)
        {
            var tracks = MatroskaFragment.ParseTracksSignature(fragment.Tracks);
            lock (HeaderGate)
            {
                if (Tracks is not null && Tracks != tracks) throw new InvalidDataException("Blu-ray track configuration changed between DV fragments.");
                Tracks = tracks;
            }
        }
    }
    private readonly ConcurrentDictionary<string, Presentation> plans = new();
    private readonly SemaphoreSlim planGate = new(1);
    private readonly Timer cleanup;
    internal DvSessionCache Cache { get; }
    internal DvOptions Options { get; }
    internal bool Available { get; }
    internal bool RemuxAvailable => File.Exists(Options.Ffmpeg);
    private readonly DvFragmentBuilder builder;
    private readonly ILogger logger;
    private readonly string engineStamp;
    internal DvRuntime(DvOptions options, ILogger logger)
    {
        Options = options; this.logger = logger; builder = new(options.Ffmpeg, options.Library, logger: logger);
        if (options.Available)
        {
            try { using var native = new DoviNative(options.Library); Available = true; }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { logger.LogWarning("Infuse DV: native dependency unavailable ({Type}); feature cannot intercept media.", e.GetType().Name); }
        }
        engineStamp = string.Join('|', new[] { options.Ffmpeg, options.Library }.Select(p => { var f = new FileInfo(p.Length == 0 ? "." : p); return f.Exists ? f.FullName + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks : p; }));
        Cache = new(options.SessionMiB * 1048576L, options.GlobalMiB * 1048576L);
        cleanup = new Timer(_ => { Cache.Trim(); TrimPlans(); builder.Trim(); }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    internal async Task<Presentation> Describe(JsonElement source, CancellationToken token, bool convertDolbyVision = true)
    {
        if (convertDolbyVision ? !Available : !RemuxAvailable) throw new InvalidOperationException("Matroska dependencies are not configured or cannot be loaded.");
        var key = (convertDolbyVision ? "dv:" : "remux:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.GetRawText())));
        if (plans.TryGetValue(key, out var hit) && hit.Plan.IsCurrent) { hit.Access = DateTime.UtcNow; return hit; }
        await planGate.WaitAsync(token);
        try
        {
            if (plans.TryGetValue(key, out hit) && hit.Plan.IsCurrent) { hit.Access = DateTime.UtcNow; return hit; }
            var plan = await Task.Run(() => DvDiscPlan.Create(source, token, engineStamp, convertDolbyVision), token);
            hit = new(plan); plans[key] = hit; TrimPlans(); return hit;
        }
        finally { planGate.Release(); }
    }
    private void TrimPlans()
    {
        foreach (var pair in plans.OrderByDescending(p => p.Value.Access).Skip(32).Concat(plans.Where(p => p.Value.Access < DateTime.UtcNow.AddMinutes(-10))).DistinctBy(p => p.Key)) plans.TryRemove(pair.Key, out _);
    }
    internal async Task<DvSessionCache.Lease> Fragment(Presentation presentation, string session, int index, CancellationToken token)
    {
        var plan = presentation.Plan; var segment = plan.Segments[index];
        return await Cache.Get(session, plan.Signature + ":" + index, segment.SlotBytes, async cancel =>
        {
            var result = await builder.Build(plan, segment, cancel); presentation.Verify(result);
            logger.LogInformation("Infuse MKV: {Mode}; playlist {Playlist}; segment {Segment}/{Total}; at {Time} ms; {Frames} converted pictures; {Bytes} output bytes; {Input} input bytes; {Elapsed} ms; copy video/audio.",
                plan.ConvertDolbyVision ? "P7→P8.1; EL residual discarded" : "Remux; original video preserved", plan.Playlist, index + 1, plan.Segments.Count, segment.ApproxStartMs, result.Pictures, result.Cluster.Length, result.InputBytes, result.ElapsedMs);
            return result;
        }, token);
    }
    internal async Task<byte[]> Header(Presentation presentation, string session, CancellationToken token)
    {
        await presentation.HeaderGate.WaitAsync(token);
        try
        {
            if (presentation.Header is null)
            {
                using var first = await Fragment(presentation, session, 0, token);
                presentation.Header = VirtualMatroska.Header(presentation.Plan, first.Fragment);
            }
            return presentation.Header;
        }
        finally { presentation.HeaderGate.Release(); }
    }
    public void Dispose() { cleanup.Dispose(); Cache.Dispose(); plans.Clear(); }
}
