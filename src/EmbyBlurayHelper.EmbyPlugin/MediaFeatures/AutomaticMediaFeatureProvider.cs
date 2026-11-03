using System.Globalization;
using EmbyBlurayHelper.EmbyPlugin.Media;
using EmbyBlurayHelper.ProbeWrapper.MediaFeatures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbyBlurayHelper.EmbyPlugin.MediaFeatures;

// Emby's post-metadata stage covers ISO as well as folder discs. No timer,
// playback listener or whole-library polling is needed for new additions.
public sealed class AutomaticMediaFeatureProvider : ICustomMetadataProvider<Movie>, IHasOrder, IHasItemChangeMonitor
{
    private readonly ILibraryManager library;
    private readonly IMediaProbeManager probe;
    private readonly IItemRepository repository;
    private readonly ILogger log;
    public AutomaticMediaFeatureProvider(ILibraryManager library, IMediaProbeManager probe, IItemRepository repository, ILogger log)
    { this.library = library; this.probe = probe; this.repository = repository; this.log = log; }
    public string Name => "EmbyBlurayHelper audio/video features";
    public int Order => 1000;

    private static bool Disc(BaseItem item) => item is Video && !string.IsNullOrWhiteSpace(item.Path) &&
        (Path.GetExtension(item.Path).Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
         File.Exists(Path.Combine(item.Path, "BDMV", "index.bdmv")) ||
         File.Exists(Path.Combine(item.Path, "index.bdmv")));
    private static string Prefix(BaseItem item) => item.InternalId.ToString(CultureInfo.InvariantCulture) + "|";
    private static string Stamp(BaseItem item)
    {
        var path = File.Exists(item.Path) ? item.Path : File.Exists(Path.Combine(item.Path, "BDMV", "index.bdmv"))
            ? Path.Combine(item.Path, "BDMV", "index.bdmv") : Path.Combine(item.Path, "index.bdmv");
        var file = new FileInfo(path);
        return Prefix(item) + item.Path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }
    public bool HasChanged(BaseItem item, LibraryOptions options, IDirectoryService directory) =>
        Plugin.Instance.Configuration.EnableAutomaticMediaFeatures && Disc(item) &&
        (item.DateLastRefreshed == default || Plugin.Instance.Configuration.AutomaticFeatureChecks.Any(s =>
            s.StartsWith(Prefix(item), StringComparison.Ordinal) && s != Stamp(item)));

    public async Task<ItemUpdateType> FetchAsync(MetadataResult<Movie> result, MetadataRefreshOptions options,
        LibraryOptions libraryOptions, CancellationToken token)
    {
        var item = result.Item;
        if (!Plugin.Instance.Configuration.EnableAutomaticMediaFeatures || !Disc(item)) return ItemUpdateType.None;
        await Plugin.ProbeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var checks = Plugin.Instance.Configuration.AutomaticFeatureChecks;
            var force = options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh;
            if (!force && (checks.Contains(Stamp(item)) || (item.DateLastRefreshed != default &&
                !checks.Any(s => s.StartsWith(Prefix(item), StringComparison.Ordinal))))) return ItemUpdateType.None;
            log.Info("MediaFeatures: automatic scan enrichment started for {0} ({1}).", item.Name, item.InternalId);
            var sources = item.GetMediaSources(false, false, libraryOptions);
            foreach (var source in sources.Where(s => BluRaySourceResolver.IsBluRaySource(s.Container, s.Path)))
            {
                var started = DateTime.UtcNow;
                var media = await BluRayMediaProbe.Probe(probe, source, token).ConfigureAwait(false);
                if (!MediaFeatureReceipt.Confirm(source.Path, started, out var verification))
                    throw new InvalidOperationException("Supplementary feature verification did not complete: " + verification);
                var ownerId = library.GetInternalId(item.InternalId.ToString(CultureInfo.InvariantCulture), source.Id);
                if (ownerId != item.InternalId) continue; // Another version is handled by its own scan.
                var existing = source.MediaStreams ?? new List<MediaStream>();
                var streams = existing.Any(s => s.Type == MediaStreamType.Video) && existing.Any(s => s.Type == MediaStreamType.Audio)
                    ? FeatureStreamMerge.Merge(existing, media.MediaStreams) : StreamMerge.Merge(media.MediaStreams, existing);
                repository.SaveMediaStreams(ownerId, streams, token);
                if (item.RunTimeTicks is null or <= 0) item.RunTimeTicks = media.RunTimeTicks;
                if (item.Size <= 0) item.Size = media.Size ?? 0;
                var confirmed = streams.Where(s => !s.IsExternal &&
                    (s.ExtendedVideoType == ExtendedVideoTypes.DolbyVision || s.Profile?.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(s => "#" + s.Index + " " + s.Codec + ": " +
                        (s.ExtendedVideoType == ExtendedVideoTypes.DolbyVision ? s.ExtendedVideoSubType.ToString() : s.Profile));
                log.Info("MediaFeatures: saved features for {0} ({1}), source {2}: [{3}]. Original technical fields retained.",
                    item.Name, item.InternalId, source.Id, string.Join("; ", confirmed));
            }
            // Change monitors can inspect this list concurrently. Publish a new
            // snapshot instead of mutating a list another scan is enumerating.
            Plugin.Instance.Configuration.AutomaticFeatureChecks = checks
                .Where(s => !s.StartsWith(Prefix(item), StringComparison.Ordinal)).Concat(new[] { Stamp(item) }).ToList();
            Plugin.Instance.SaveConfiguration();
            log.Info("MediaFeatures: automatic scan enrichment finished for {0} ({1}).", item.Name, item.InternalId);
            return ItemUpdateType.MetadataImport;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            log.Warn("MediaFeatures: supplementary scan enrichment skipped for {0}; original media information retained. {1}: {2}", item.Name, e.GetType().Name, e.Message);
            return ItemUpdateType.None;
        }
        finally { Plugin.ProbeGate.Release(); }
    }
}
