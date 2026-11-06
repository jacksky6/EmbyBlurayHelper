using System.Globalization;
using EmbyBlurayHelper.EmbyPlugin.Media;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace EmbyBlurayHelper.EmbyPlugin.Chapters;

public sealed class AutomaticChapterProvider(ILibraryManager library, IItemRepository repository, ILogger log)
    : ICustomMetadataProvider<Movie>, IHasOrder, IHasItemChangeMonitor
{
    public string Name => "EmbyBlurayHelper native disc chapters";
    public int Order => 1100;
    private static bool Disc(BaseItem item) => !string.IsNullOrWhiteSpace(item.Path) && (Path.GetExtension(item.Path).Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(Path.Combine(item.Path, "BDMV", "index.bdmv")) || File.Exists(Path.Combine(item.Path, "index.bdmv")));
    public bool HasChanged(BaseItem item, LibraryOptions options, IDirectoryService directory) =>
        Plugin.Instance.Configuration.EnableNativeChapters && Disc(item) && LibraryProcessingScope.Allowed(library, item) && item.DateLastRefreshed == default;
    public async Task<ItemUpdateType> FetchAsync(MetadataResult<Movie> result, MetadataRefreshOptions options, LibraryOptions libraryOptions, CancellationToken token)
    {
        var item = result.Item;
        if (!Plugin.Instance.Configuration.EnableNativeChapters || !Disc(item) || !LibraryProcessingScope.Allowed(library, item)) return ItemUpdateType.None;
        await Plugin.ProbeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!LibraryProcessingScope.Allowed(library, item)) return ItemUpdateType.None;
            // Preserve user chapters/markers even if navigation reading fails.
            // This also protects against Emby's final metadata-result commit.
            var previous = repository.GetChapters(item);
            if (previous.Count > 0) result.Chapters = previous;
            foreach (var source in item.GetMediaSources(false, false, libraryOptions).Where(s => BluRaySourceResolver.IsBluRaySource(s.Container, s.Path)))
            {
                if (!LibraryProcessingScope.Allowed(library, item, source.Path)) continue;
                var id = item.InternalId.ToString(CultureInfo.InvariantCulture);
                var target = BluRaySourceResolver.Resolve(library, id, source.Id);
                if (target.OwnerId != item.InternalId) continue;
                if (options.MetadataRefreshMode != MetadataRefreshMode.FullRefresh && Plugin.Instance.Configuration.ChapterReceipts.Any(r => r.OwnerId == target.OwnerId && r.Path == source.Path &&
                    r.Signature == NativeChapterMerge.Signature(repository.GetChapters(item))))
                {
                    // Emby writes MetadataResult.Chapters again after all providers.
                    // Carry the preserved list through its final commit as well.
                    result.Chapters = repository.GetChapters(item);
                    continue;
                }
                await new ChapterWriter(library, repository).Fill(target, "", token).ConfigureAwait(false);
                result.Chapters = repository.GetChapters(item);
                var saved = NativeChapterMerge.Normal(repository.GetChapters(item));
                var receipt = Plugin.Instance.Configuration.ChapterReceipts.FirstOrDefault(r => r.OwnerId == target.OwnerId && r.Path == source.Path);
                log.Info("NativeChapters: automatic chapters saved for {0} ({1}), playlist {2}, {3} chapters. Seconds: [{4}].",
                    item.Name, id, receipt?.Playlist ?? "existing native chapters retained", saved.Length, string.Join(", ", saved.Select(c => (c.StartPositionTicks / (double)TimeSpan.TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture))));
            }
            return ItemUpdateType.MetadataImport;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) { log.Warn("NativeChapters: retained existing chapters for {0}: {1}", item.Name, e.Message); return ItemUpdateType.None; }
        finally { Plugin.ProbeGate.Release(); }
    }
}
