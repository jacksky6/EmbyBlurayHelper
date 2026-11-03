using System.Collections;
using System.Reflection;
using EmbyBlurayHelper.EmbyPlugin.Thumbnails;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

var checks = 0;
var queues = 0;
var options = new LibraryOptions { EnableChapterImageExtraction = true, ExtractChapterImagesDuringLibraryScan = false };
EventHandler<TaskCompletionEventArgs>? completed = null;
var manager = Stub.Create<ITaskManager>((m, a) =>
{
    if (m.Name == "add_TaskCompleted") completed += (EventHandler<TaskCompletionEventArgs>)a![0]!;
    if (m.Name == "remove_TaskCompleted") completed -= (EventHandler<TaskCompletionEventArgs>)a![0]!;
    if (m.Name == "QueueIfNotRunning") { Check(m.GetGenericArguments().Single() == typeof(DiscThumbnailTask), "only disc supplement is queued"); queues++; }
    return null;
});
var library = Stub.Create<ILibraryManager>((m, _) =>
{
    if (m.Name != "GetVirtualFolders") throw new InvalidOperationException(m.Name);
    var element = m.ReturnType.IsArray ? m.ReturnType.GetElementType()! : m.ReturnType.GetGenericArguments().Single();
    var folder = Activator.CreateInstance(element)!;
    element.GetProperty("LibraryOptions")!.SetValue(folder, options);
    var array = Array.CreateInstance(element, 1); array.SetValue(folder, 0);
    if (m.ReturnType.IsArray) return array;
    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!; list.Add(folder); return list;
});
var logger = Stub.Create<ILogger>((_, _) => null);
var logs = Stub.Create<ILogManager>((m, _) => m.Name == "GetLogger" ? logger : null);
var entry = new DiscThumbnailEntryPoint(manager, library, logs);
entry.Run(); Check(completed is not null, "entry point subscribes to native task completion");
var native = new NativeTask();
var worker = Stub.Create<IScheduledTaskWorker>((m, _) => m.Name == "get_ScheduledTask" ? native : null);
void Fire(TaskCompletionStatus status) => completed!(null, new TaskCompletionEventArgs { Task = worker, Result = new TaskResult { Status = status } });
Fire(TaskCompletionStatus.Completed); Check(queues == 1, "task-only library follows a successful native task");
foreach (var status in Enum.GetValues<TaskCompletionStatus>().Where(s => s != TaskCompletionStatus.Completed)) Fire(status);
Check(queues == 1, "canceled, failed or aborted native tasks cannot trigger extraction");
native.ExtractImages = false; Fire(TaskCompletionStatus.Completed); Check(queues == 1, "native maintenance with image extraction off cannot trigger screenshots");
native.ExtractImages = true; native.Key = "RefreshLibrary"; Fire(TaskCompletionStatus.Completed); Check(queues == 1, "library scans do not trigger task-only screenshots");
native.Key = "EmbyBlurayHelperDiscThumbnails"; Fire(TaskCompletionStatus.Completed); Check(queues == 1, "supplement completion cannot queue itself in a loop");
native.Key = "RefreshChapterImages"; options.EnableChapterImageExtraction = false; Fire(TaskCompletionStatus.Completed); Check(queues == 1, "all libraries set to Never suppress the supplement");
options.EnableChapterImageExtraction = true; options.ExtractChapterImagesDuringLibraryScan = true; Fire(TaskCompletionStatus.Completed); Check(queues == 2, "scan-and-task mode also follows native task completion");
entry.Dispose(); Check(completed is null, "shutdown unsubscribes without leaving callbacks");
var task = new DiscThumbnailTask(null!, null!, null!, null!, logs);
Check(!task.GetDefaultTriggers().Any(), "no duplicate default timer or independent schedule");
Console.WriteLine($"Passed {checks} native thumbnail scheduling checks.");
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }

public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var value = DispatchProxy.Create<T, Stub>(); ((Stub)(object)value).Handler = handler; return value; }
}
public class NativeTask : IScheduledTask
{
    public string Name => "Native thumbnail fixture";
    public string Key { get; set; } = "RefreshChapterImages";
    public bool ExtractImages { get; set; } = true;
    public string Description => "";
    public string Category => "Library";
    public Task Execute(CancellationToken token, IProgress<double> progress) => Task.CompletedTask;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
