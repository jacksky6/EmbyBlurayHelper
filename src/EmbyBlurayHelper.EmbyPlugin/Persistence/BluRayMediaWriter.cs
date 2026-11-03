using EmbyBlurayHelper.EmbyPlugin.Media;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.MediaInfo;

namespace EmbyBlurayHelper.EmbyPlugin.Persistence;

internal sealed class BluRayMediaWriter
{
    private readonly ILibraryManager library;
    private readonly IItemRepository repository;
    public BluRayMediaWriter(ILibraryManager library, IItemRepository repository)
    { this.library = library; this.repository = repository; }

    public SavedBluRayMedia Save(BluRayProbeTarget target, MediaInfo media, CancellationToken token, bool repairPlaylist = false)
    {
        var owner = library.GetItemById(target.OwnerId);
        var current = owner is null ? null : BluRaySourceResolver.FindCurrent(library, owner, target.Source);
        if (owner is null || current is null)
            throw new InvalidOperationException("The media source changed while probing; no media information was saved.");
        // Only an explicit repair of an ISO selected by an older Helper can replace
        // its wrong title's internal streams and duration. External subtitles survive.
        if (repairPlaylist && !string.Equals(Path.GetExtension(owner.Path), ".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Playlist repair is supported only for ISO sources.");
        var changedTitle = repairPlaylist && (owner.RunTimeTicks is null or <= 0 || media.RunTimeTicks is null or <= 0 ||
            Math.Abs(owner.RunTimeTicks.Value - media.RunTimeTicks.Value) > TimeSpan.TicksPerSecond);
        var streams = StreamMerge.Merge(media.MediaStreams, changedTitle
            ? current.MediaStreams?.Where(s => s.IsExternal) : current.MediaStreams);
        var runtime = repairPlaylist ? MediaFacts.Runtime(media.RunTimeTicks, owner.RunTimeTicks)
            : MediaFacts.Runtime(owner.RunTimeTicks, media.RunTimeTicks);
        // ISO size is the entire file, not the virtual main-title stream returned by ffprobe.
        long? size = media.Size;
        if (string.Equals(Path.GetExtension(owner.Path), ".iso", StringComparison.OrdinalIgnoreCase))
            size = File.Exists(owner.Path) ? new FileInfo(owner.Path).Length : (long?)null;
        var savedSize = MediaFacts.Size(owner.Size, size);
        token.ThrowIfCancellationRequested();
        repository.SaveMediaStreams(target.OwnerId, streams, token);
        if (owner.RunTimeTicks != runtime || owner.Size != savedSize)
        {
            var oldRuntime = owner.RunTimeTicks;
            var oldSize = owner.Size;
            owner.RunTimeTicks = runtime;
            owner.Size = savedSize;
            try
            {
                // Emby repository update without running NFO/title/artwork writers.
                library.UpdateItems(new List<BaseItem> { owner }, owner.GetParent(), ItemUpdateType.MetadataImport,
                    true, false, null!, token);
            }
            catch { owner.RunTimeTicks = oldRuntime; owner.Size = oldSize; throw; }
        }
        return new SavedBluRayMedia(streams, runtime, savedSize);
    }
}
