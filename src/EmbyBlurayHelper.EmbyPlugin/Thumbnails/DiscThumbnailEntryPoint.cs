using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

public sealed class DiscThumbnailEntryPoint(ITaskManager tasks, ILibraryManager library, ILogManager logs) : IServerEntryPoint
{
    public void Run() => tasks.TaskCompleted += Completed;
    private void Completed(object? sender, TaskCompletionEventArgs args)
    {
        if (args.Task.ScheduledTask.Key != DiscThumbnailPolicy.NativeTaskKey || args.Result.Status != TaskCompletionStatus.Completed ||
            args.Task.ScheduledTask.GetType().GetProperty("ExtractImages")?.GetValue(args.Task.ScheduledTask) is false) return;
        try
        {
            if (!library.GetVirtualFolders().Any(f => DiscThumbnailPolicy.Enabled(f.LibraryOptions, false))) return;
            logs.GetLogger("DiscThumbnails").Info("DiscThumbnails: Emby thumbnail task completed; queueing disc supplement with the same library settings.");
            tasks.QueueIfNotRunning<DiscThumbnailTask>();
        }
        catch (Exception e) { logs.GetLogger("DiscThumbnails").Warn("DiscThumbnails: could not queue disc supplement: {0}", e.Message); }
    }
    public void Dispose() => tasks.TaskCompleted -= Completed;
}
