using EmbyBlurayHelper.EmbyPlugin.Chapters;
using EmbyBlurayHelper.EmbyPlugin.Media;

namespace EmbyBlurayHelper.EmbyPlugin;

public sealed partial class BlurayHelperService
{
    public object Get(ChapterConfigurationRequest request) => new { enabled = Plugin.Instance.Configuration.EnableNativeChapters,
        readerPath = Plugin.Instance.Configuration.ChapterReaderPath, checkedItems = Plugin.Instance.Configuration.ChapterReceipts.Count,
        thumbnails = ThumbnailStatus() };
    private object ThumbnailStatus()
    {
        var worker = ThumbnailTaskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "EmbyBlurayHelperDiscThumbnails");
        return new { available = true, followsEmby = true,
            worker = new { state = worker?.State.ToString() ?? "Unavailable", progress = worker?.CurrentProgress,
                lastStatus = worker?.LastExecutionResult?.Status.ToString(), error = worker?.LastExecutionResult?.ErrorMessage },
            libraries = LibraryManager.GetVirtualFolders().Select(f => new { id = f.Id, name = f.Name,
                mode = !f.LibraryOptions.EnableChapterImageExtraction ? "never" : f.LibraryOptions.ExtractChapterImagesDuringLibraryScan ? "scanandtask" : "task",
                interval = Thumbnails.DiscThumbnailPolicy.Interval(f.LibraryOptions), saveLocal = f.LibraryOptions.SaveLocalThumbnailSets }).ToArray() };
    }
    public async Task<object> Post(ChapterConfigurationRequest request)
    {
        await ProbeGate.WaitAsync(Request.CancellationToken).ConfigureAwait(false);
        try
        {
            if (request.Enabled) ChapterProcess.Validate(request.ReaderPath);
            Plugin.Instance.Configuration.EnableNativeChapters = request.Enabled;
            Plugin.Instance.Configuration.ChapterReaderPath = request.ReaderPath;
            Plugin.Instance.SaveConfiguration();
            return Get(request);
        }
        finally { ProbeGate.Release(); }
    }
    public object Post(InspectChaptersRequest request) => new ChapterWriter(LibraryManager, ItemRepository)
        .Inspect(BluRaySourceResolver.Resolve(LibraryManager, request.ItemId, request.MediaSourceId));
    public async Task<object> Post(FillChaptersRequest request)
    {
        await ProbeGate.WaitAsync(Request.CancellationToken).ConfigureAwait(false);
        try
        {
            var target = BluRaySourceResolver.Resolve(LibraryManager, request.ItemId, request.MediaSourceId);
            LibraryProcessingScope.RequireAllowed(LibraryManager, target.Item, target.Source.Path);
            var result = await new ChapterWriter(LibraryManager, ItemRepository).Fill(target, request.ExpectedSignature, Request.CancellationToken).ConfigureAwait(false);
            Logger.Info("NativeChapters: saved and verified original chapters for {0} ({1}), source {2}. Audio/video/subtitles and skip markers retained.", target.Item.Name, request.ItemId, target.Source.Id);
            return result;
        }
        finally { ProbeGate.Release(); }
    }
}
