namespace EmbyBlurayHelper.App;

// Runs the chosen library completions one after another, so the media disks are
// read by one task at a time: language and runtime, then specs, then chapters.
// Each task keeps its own plan, results and persistence; this only chains them.
// The chain itself is not persisted: after a restart, finished steps keep their
// results and the remaining ones can be started again.
internal sealed class LibraryPipeline : IAsyncDisposable
{
    internal sealed record StepInput(string Task, string PlanId, bool IncludeVerified = false);
    private sealed class Step(string key, string name)
    {
        public string Key { get; } = key; public string Name { get; } = name;
        public string Status { get; set; } = "waiting"; public string Message { get; set; } = "";
    }
    private sealed record Runner(string Key, string Name, Func<(bool Active, string Status, string Message)> State,
        Func<StepInput, string[], object> Fill, Func<object> Cancel, Func<object> Scan);
    private static readonly string[] Order = ["language", "features", "chapters"];
    private readonly Dictionary<string, Runner> tasks;
    private readonly HelperLog log;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private string status = "idle", message = "";
    private string? current;
    private string[] libraryIds = [];
    private List<Step> steps = [];
    private DateTimeOffset? startedAt, finishedAt;
    private bool cancelRequested;
    private Task worker = Task.CompletedTask;

    public LibraryPipeline(LibraryLanguageTask language, MediaFeatureBatch features, NativeChapterBatch chapters, HelperLog log)
    {
        this.log = log;
        tasks = new Runner[]
        {
            new("language", "语言与时长", () => language.State, (s, ids) => language.Fill(s.PlanId, ids, s.IncludeVerified), language.Cancel, language.Scan),
            new("features", "音视频规格", () => features.State, (s, ids) => features.Fill(s.PlanId, ids, s.IncludeVerified), features.Cancel, features.Scan),
            new("chapters", "原盘章节", () => chapters.State, (s, ids) => chapters.Fill(s.PlanId, ids, s.IncludeVerified), chapters.Cancel, chapters.Scan)
        }.ToDictionary(t => t.Key);
    }

    private bool Running => status is "running" or "cancelling";
    internal bool Active { get { lock (gate) return Running; } }

    internal object Snapshot()
    {
        lock (gate) return new { status, message, current, libraryIds, startedAt, finishedAt, active = Running,
            steps = steps.Select(s => new { task = s.Key, name = s.Name, status = s.Status, message = s.Message }).ToArray() };
    }

    // All three checks only read Emby's saved data, so they run together.
    internal object ScanAll()
    {
        lock (gate) if (Running) throw new InvalidOperationException("补全任务正在运行，请等待完成或先停止。");
        var results = new Dictionary<string, string>();
        foreach (var task in tasks.Values)
            try { task.Scan(); results[task.Key] = "started"; }
            catch (InvalidOperationException e) { results[task.Key] = e.Message; }
        return new { results };
    }

    internal object Start(string[] selected, StepInput[] chosen)
    {
        lock (gate)
        {
            if (Running) throw new InvalidOperationException("补全任务正在运行，请等待完成或先停止。");
            if (selected.Length == 0) throw new InvalidOperationException("请至少选择一个媒体库。");
            var ordered = chosen.Where(s => tasks.ContainsKey(s.Task)).DistinctBy(s => s.Task).OrderBy(s => Array.IndexOf(Order, s.Task)).ToArray();
            if (ordered.Length == 0) throw new InvalidOperationException("请至少选择一项补全内容。");
            var busy = tasks.Values.FirstOrDefault(t => t.State().Active);
            if (busy is not null) throw new InvalidOperationException($"「{busy.Name}」正在检查或补全，请等待完成后再开始。");
            libraryIds = selected.Distinct(StringComparer.Ordinal).ToArray();
            steps = ordered.Select(s => new Step(s.Task, tasks[s.Task].Name)).ToList();
            status = "running"; message = $"共 {steps.Count} 项，按顺序补全。"; cancelRequested = false; current = null;
            startedAt = DateTimeOffset.UtcNow; finishedAt = null;
            var ids = libraryIds;
            worker = Task.Run(() => Run(ordered, ids));
            log.Add("Information", "LibraryPipeline", $"开始媒体库补全：{string.Join("、", steps.Select(s => s.Name))}。");
            return Snapshot();
        }
    }

    private async Task Run(StepInput[] ordered, string[] ids)
    {
        foreach (var input in ordered)
        {
            var task = tasks[input.Task];
            Step step; lock (gate) { step = steps.First(s => s.Key == input.Task); if (cancelRequested) break; current = task.Key; }
            try
            {
                task.Fill(input, ids);
                lock (gate) { step.Status = "running"; step.Message = "正在补全"; }
            }
            // Nothing to do, a stale plan or a disabled feature: report it and go on.
            catch (InvalidOperationException e)
            {
                lock (gate) { step.Status = "skipped"; step.Message = e.Message; }
                log.Add("Warning", "LibraryPipeline", $"「{task.Name}」未执行：{e.Message}");
                continue;
            }
            try { while (task.State().Active) await Task.Delay(1000, shutdown.Token); }
            catch (OperationCanceledException) { return; }
            var (_, final, text) = task.State();
            lock (gate) { step.Status = final is "completed" ? "completed" : final is "cancelled" ? "cancelled" : "failed"; step.Message = text; }
        }
        lock (gate)
        {
            foreach (var step in steps.Where(s => s.Status == "waiting")) { step.Status = "cancelled"; step.Message = "已停止，未执行"; }
            status = cancelRequested ? "cancelled" : "completed"; current = null; finishedAt = DateTimeOffset.UtcNow;
            static string Outcome(string s) => s switch { "completed" => "完成", "skipped" => "未执行", "cancelled" => "已停止", _ => "未完成" };
            message = string.Join("；", steps.Select(s => $"{s.Name}：{Outcome(s.Status)}")) + "。";
            log.Add(steps.All(s => s.Status == "completed") ? "Information" : "Warning", "LibraryPipeline", "媒体库补全结束。" + message);
        }
    }

    internal object Cancel()
    {
        lock (gate)
        {
            if (!Running) return Snapshot();
            cancelRequested = true; status = "cancelling"; message = "正在停止；当前项会在已提交给 Emby 的处理结束后停下，后续项不再执行。";
            if (current is not null) tasks[current].Cancel();
            return Snapshot();
        }
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        try { await worker; } catch (OperationCanceledException) { }
        shutdown.Dispose();
    }
}
