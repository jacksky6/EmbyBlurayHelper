using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

// No default triggers: Emby's existing RefreshChapterImages task owns the
// schedule. A separate worker exposes real progress/cancellation without
// patching Emby's private classes or changing the original task's triggers.
public sealed class DiscThumbnailTask(ILibraryManager library, IItemRepository repository,
    MediaBrowser.Controller.IServerApplicationHost host, IFileSystem files, ILogManager logs) : IScheduledTask
{
    public string Name => "原盘章节截图（跟随 Emby）";
    public string Key => "EmbyBlurayHelperDiscThumbnails";
    public string Category => "Library";
    public string Description => "跟随 Emby 视频预览缩略图提取任务，按各媒体库设置处理 BDMV / Blu-ray ISO。无需另设计划；可查看进度、停止任务。";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        var log = logs.GetLogger("DiscThumbnails");
        var items = library.GetItemList(new InternalItemsQuery { Recursive = true, MediaTypes = new[] { "Video" }, GroupByPresentationUniqueKey = false })
            .OfType<Video>().Where(v => DiscThumbnailService.IsDisc(v) && LibraryProcessingScope.Allowed(library, v) && DiscThumbnailPolicy.Enabled(library.GetLibraryOptions(v), false)).ToArray();
        log.Info("DiscThumbnails: Emby-following task started. {0} disc items in enabled libraries.", items.Length);
        var service = new DiscThumbnailService(library, repository, host, files, log);
        var failures = 0;
        for (var i = 0; i < items.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var position = i;
            try { await service.Extract(items[i], false, token, p => progress.Report((position + p / 100) / items.Length * 100)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { failures++; log.Warn("DiscThumbnails: [{0}/{1}] {2} ({3}) failed; existing images retained: {4}", i + 1, items.Length, items[i].Name, items[i].InternalId, e.Message); }
            progress.Report((i + 1.0) / items.Length * 100);
        }
        progress.Report(100);
        log.Info("DiscThumbnails: task finished. {0} items, {1} failures (see per-item logs).", items.Length, failures);
        if (failures > 0) throw new InvalidOperationException(failures + " 个原盘截图失败，请查看 DiscThumbnails 日志。已完成图片保留，可重跑任务重试缺失部分。");
    }
}
