using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

public sealed class AutomaticDiscThumbnailProvider(ILibraryManager library, IItemRepository repository,
    MediaBrowser.Controller.IServerApplicationHost host, IFileSystem files, ILogger log)
    : ICustomMetadataProvider<Movie>, IHasOrder, IHasItemChangeMonitor
{
    public string Name => "EmbyBlurayHelper disc thumbnails (Emby library settings)";
    public int Order => 1200;
    public bool HasChanged(BaseItem item, LibraryOptions options, IDirectoryService directory) =>
        item is Movie movie && DiscThumbnailService.IsDisc(movie) && LibraryProcessingScope.Allowed(library, item) && DiscThumbnailPolicy.Enabled(options, true) && item.DateLastRefreshed == default;

    public async Task<ItemUpdateType> FetchAsync(MetadataResult<Movie> result, MetadataRefreshOptions refresh, LibraryOptions options, CancellationToken token)
    {
        if (!DiscThumbnailService.IsDisc(result.Item) || !LibraryProcessingScope.Allowed(library, result.Item) || !DiscThumbnailPolicy.Enabled(options, true)) return ItemUpdateType.None;
        var previous = result.Chapters ?? repository.GetChapters(result.Item);
        if (previous.Count > 0) result.Chapters = previous;
        try
        {
            result.Chapters = await new DiscThumbnailService(library, repository, host, files, log)
                .Extract(result.Item, true, token, stagedChapters: previous).ConfigureAwait(false);
            return ItemUpdateType.MetadataImport;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) { log.Warn("DiscThumbnails: scan retained existing images for {0}: {1}", result.Item.Name, e.Message); return ItemUpdateType.None; }
    }
}
