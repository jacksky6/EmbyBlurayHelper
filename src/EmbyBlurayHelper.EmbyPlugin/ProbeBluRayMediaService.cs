using EmbyBlurayHelper.EmbyPlugin.Media;
using EmbyBlurayHelper.EmbyPlugin.Persistence;
using EmbyBlurayHelper.EmbyPlugin.MediaFeatures;
using EmbyBlurayHelper.ProbeWrapper.MediaFeatures;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Services;

namespace EmbyBlurayHelper.EmbyPlugin;

// HTTP and authentication only; validation, probing and persistence have their own modules.
public sealed partial class BlurayHelperService : BaseApiService
{
    private static SemaphoreSlim ProbeGate => Plugin.ProbeGate;
    [AutoInjectProperty]
    public IMediaProbeManager MediaProbeManager { get; set; } = null!;
    [AutoInjectProperty]
    public IItemRepository ItemRepository { get; set; } = null!;
    [AutoInjectProperty]
    public IServerConfigurationManager ServerConfigurationManager { get; set; } = null!;
    [AutoInjectProperty]
    public MediaBrowser.Model.Tasks.ITaskManager ThumbnailTaskManager { get; set; } = null!;

    public object Get(BlurayHelperStatusRequest request) => new
    {
        status = "ready",
        version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        capabilities = new[] { "media-streams", "missing-runtime", "missing-size", "bdmv-direct-probe", "playlist-repair", "media-features", "automatic-media-features", "media-features-verification", "native-chapters", "disc-thumbnails-emby-settings" }
    };

    public object Get(MediaFeatureConfigurationRequest request) => new
    { enabled = Plugin.Instance.Configuration.EnableAutomaticMediaFeatures, checkedItems = Plugin.Instance.Configuration.AutomaticFeatureChecks.Count,
        originalTrackTitles = ServerConfigurationManager.Configuration.EnableOriginalTrackTitles };

    public async Task<object> Post(MediaFeatureConfigurationRequest request)
    {
        await ProbeGate.WaitAsync(Request.CancellationToken).ConfigureAwait(false);
        try
        {
            Plugin.Instance.Configuration.EnableAutomaticMediaFeatures = request.Enabled;
            Plugin.Instance.SaveConfiguration();
            if (request.ShowOriginalTrackTitles.HasValue && ServerConfigurationManager.Configuration.EnableOriginalTrackTitles != request.ShowOriginalTrackTitles.Value)
            {
                ServerConfigurationManager.Configuration.EnableOriginalTrackTitles = request.ShowOriginalTrackTitles.Value;
                ServerConfigurationManager.SaveConfiguration();
            }
            return Get(request);
        }
        finally { ProbeGate.Release(); }
    }

    public async Task<object> Post(ProbeMediaFeaturesRequest request)
    {
        await ProbeGate.WaitAsync(Request.CancellationToken).ConfigureAwait(false);
        try
        {
            var target = BluRaySourceResolver.Resolve(LibraryManager, request.ItemId, request.MediaSourceId);
            var started = DateTime.UtcNow;
            var media = await BluRayMediaProbe.Probe(MediaProbeManager, target.Source, Request.CancellationToken).ConfigureAwait(false);
            if (!MediaFeatureReceipt.Confirm(target.Source.Path, started, out var verification))
                throw new InvalidOperationException("增强规格未完成核验，未写入影片信息：" + verification);
            var owner = LibraryManager.GetItemById(target.OwnerId);
            var current = owner is null ? null : BluRaySourceResolver.FindCurrent(LibraryManager, owner, target.Source);
            if (current?.MediaStreams is null) throw new InvalidOperationException("The media source changed; no features were saved.");
            var streams = FeatureStreamMerge.Merge(current.MediaStreams, media.MediaStreams);
            ItemRepository.SaveMediaStreams(target.OwnerId, streams, Request.CancellationToken);
            Logger.Info("MediaFeatures: saved supplementary audio/video features for {0} ({1}).", target.Item.Name, request.ItemId);
            return new { name = target.Item.Name, verified = true, mediaSourceId = target.Source.Id, streams = streams.Where(s => !s.IsExternal &&
                (s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video || s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio))
                .Select(s => new { s.Index, s.Codec, s.Profile, s.Title, s.Language, ExtendedVideoType = s.ExtendedVideoType.ToString(), ExtendedVideoSubType = s.ExtendedVideoSubType.ToString() }).ToArray() };
        }
        finally { ProbeGate.Release(); }
    }

    public async Task<object> Post(ProbeBluRayMediaRequest request)
    {
        await ProbeGate.WaitAsync(Request.CancellationToken).ConfigureAwait(false);
        try
        {
            var target = BluRaySourceResolver.Resolve(LibraryManager, request.ItemId, request.MediaSourceId);
            var media = await BluRayMediaProbe.Probe(MediaProbeManager, target.Source, Request.CancellationToken).ConfigureAwait(false);
            var saved = new BluRayMediaWriter(LibraryManager, ItemRepository).Save(target, media, Request.CancellationToken, request.RepairPlaylist);
            Logger.Info("Blu-ray media information saved for {0} ({1}): runtime {2}; size {3} bytes. Audio languages: [{4}]. Subtitle languages: [{5}].",
                target.Item.Name, Path.GetFileName(target.Source.Path), saved.RuntimeDisplay, saved.Size,
                saved.AudioLanguages, saved.SubtitleLanguages);
            return saved.Response(request.ItemId, target.Source.Id);
        }
        finally { ProbeGate.Release(); }
    }
}
