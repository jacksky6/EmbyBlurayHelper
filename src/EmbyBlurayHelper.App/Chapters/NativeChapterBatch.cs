using System.Diagnostics;
using System.Text.Json;

namespace EmbyBlurayHelper.App;

// Independent state and cancellation: chapter jobs never invoke the language/spec probe.
internal sealed class NativeChapterBatch : IAsyncDisposable
{
    private readonly SettingsStore settings;
    private readonly MediaFeatureCatalog catalog;
    private readonly NativeChapterService service;
    private readonly HelperLog log;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private ChapterPlan plan = new();
    private string FilePath => Path.Combine(settings.DataDirectory, "native-chapter-task.json");
    private static bool Active(string s) => s is "scanning" or "filling" or "cancelling";
    // Read by LibraryPipeline to chain fills without parsing the snapshot.
    internal (bool Active, string Status, string Message) State { get { lock (gate) return (Active(plan.Status), plan.Status, plan.Message); } }
    private static bool Done(ChapterMovie m) => m.Sources.Count > 0 && m.Sources.All(s => s.State is "managed" or "existing");
    internal NativeChapterBatch(SettingsStore settings, HttpClient client, NativeChapterService service, HelperLog log)
    {
        this.settings = settings; this.service = service; this.log = log; catalog = new(settings, client);
        try
        {
            if (!File.Exists(FilePath)) return;
            plan = JsonSerializer.Deserialize<ChapterPlan>(File.ReadAllText(FilePath), SettingsStore.Json) ?? new();
            if (Active(plan.Status)) { plan.Status = "interrupted"; plan.Message = "上次任务中断，可继续未完成的影片；已保存章节保留。"; Persist(); }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { plan = new(); log.Add("Warning", "NativeChapters", "无法读取上次章节任务，请重新检查媒体库。"); }
    }
    internal object Snapshot()
    {
        lock (gate) return new { plan.Id, plan.Status, plan.Message, plan.PersistenceWarning, plan.ScanComplete, plan.Processed, plan.Planned,
            plan.Completed, plan.Failed, plan.Skipped, plan.CurrentName, plan.IncludeVerified, plan.SelectedLibraryIds,
            active = Active(plan.Status), connectionMatches = plan.Connection == MediaFeatureCatalog.Connection(settings.Current),
            canFill = !Active(plan.Status) && plan.ScanComplete && plan.Connection == MediaFeatureCatalog.Connection(settings.Current),
            summary = new { discs = plan.Items.Count, verified = plan.Items.Count(i => i.Sources.All(s => s.State == "managed")),
                pending = plan.Items.Count(i => !Done(i)), preserved = plan.Items.Count(i => i.Sources.Any(s => s.State == "existing")) },
            libraries = plan.Libraries.Select(l => new { l.Id, l.Name, l.MoviesChecked, l.ScanComplete, discs = plan.Items.Count(i => i.LibraryIds.Contains(l.Id)),
                pending = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && !Done(i)), verified = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && i.Sources.All(s => s.State == "managed")),
                preserved = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && i.Sources.Any(s => s.State == "existing")) }).ToArray(),
            items = plan.Items.Select(i => new { i.Id, i.Name, i.LibraryIds, i.Result, i.Message, pending = !Done(i), sources = i.Sources.Select(s => new { s.Id, s.Kind, s.State, s.Count, s.Playlist }) }).ToArray() };
    }
    internal object Scan()
    {
        lock (gate)
        {
            Idle(); var config = settings.Current;
            if (config.EmbyApiKey.Length == 0) throw new InvalidOperationException("请先配置 Emby 连接。");
            plan = new() { Id = Guid.NewGuid().ToString("N"), Connection = MediaFeatureCatalog.Connection(config), Status = "scanning", Message = "正在检查原盘章节；此步骤只读取已保存信息。" };
            Launch(token => ScanCore(config, token)); return Snapshot();
        }
    }
    internal object Fill(string id, string[]? libraries, bool again)
    {
        lock (gate)
        {
            Idle(); var config = settings.Current;
            if (!config.EnableNativeChapters) throw new InvalidOperationException("请先开启原盘章节补全。");
            if (id != plan.Id || !plan.ScanComplete || plan.Connection != MediaFeatureCatalog.Connection(config)) throw new InvalidOperationException("章节计划已失效，请重新检查。");
            var selected = (libraries ?? plan.Libraries.Select(l => l.Id).ToArray()).Distinct().ToList();
            if (selected.Count == 0 || selected.Any(id => !plan.Libraries.Any(l => l.Id == id))) throw new ArgumentException("请选择计划内的媒体库；空选不会处理全部媒体库。");
            var pending = plan.Items.Where(i => i.LibraryIds.Intersect(selected).Any() && (again || !Done(i))).ToArray();
            if (pending.Length == 0) throw new InvalidOperationException("所选媒体库没有待补全影片。");
            plan.SelectedLibraryIds = selected; plan.IncludeVerified = again; plan.Status = "filling";
            plan.Planned = pending.Length; plan.Processed = plan.Completed = plan.Failed = plan.Skipped = 0;
            plan.Message = "章节任务已启动，仅读取原盘导航文件。";
            Launch(token => FillCore(config, pending, token)); return Snapshot();
        }
    }
    internal object Cancel()
    { lock (gate) { if (Active(plan.Status)) { plan.Status = "cancelling"; plan.Message = "正在停止；当前已提交的章节写入可能仍会完成。"; cancellation?.Cancel(); Persist(); } return Snapshot(); } }
    private void Idle() { if (Active(plan.Status)) throw new InvalidOperationException("章节任务正在运行，请先停止或等待完成。"); }
    private void Launch(Func<CancellationToken, Task> action)
    {
        cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var token = cancellation.Token; Persist();
        worker = Task.Run(async () =>
        {
            try { await action(token); }
            catch (Exception e)
            {
                var cancelled = e is OperationCanceledException && token.IsCancellationRequested;
                Update(() => { plan.Status = cancelled ? "cancelled" : "failed"; plan.CurrentName = "";
                    plan.Message = cancelled ? "任务已停止，可继续未完成部分。" : Error(e);
                    foreach (var item in plan.Items.Where(i => i.Result == "running")) { item.Result = "pending"; item.Message = "任务中断；已提交的章节可能仍会保存。可继续，状态变化时请重新检查。"; } });
                log.Add(cancelled ? "Information" : "Error", "NativeChapters", plan.Message);
            }
        });
    }
    private async Task ScanCore(HelperSettings config, CancellationToken token)
    {
        await service.Send(config, "", null, token); // Fail early with an actionable DLL/restart message.
        var server = await catalog.Read(config, "System/Info/Public", token);
        Update(() => plan.ServerId = MediaFeatureCatalog.Text(server, "Id"));
        if (plan.ServerId.Length == 0) throw new InvalidDataException("Emby 未返回服务器 ID。");
        var groups = await catalog.Libraries(config, token);
        Update(() => plan.Libraries = groups);
        var found = new Dictionary<string, ChapterMovie>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var discs = new List<FeatureMovie>();
            await catalog.Movies(config, group.Id, raw => { lock (gate) group.MoviesChecked++; if (MediaFeatureCatalog.Analyze(raw) is { } movie) discs.Add(movie); }, token);
            foreach (var disc in discs)
            {
                token.ThrowIfCancellationRequested();
                if (found.TryGetValue(disc.Id, out var shared)) { Update(() => shared.LibraryIds.Add(group.Id)); continue; }
                var movie = new ChapterMovie { Id = disc.Id, Name = disc.Name, LibraryIds = [group.Id] };
                foreach (var source in disc.Sources)
                {
                    var entry = new ChapterSource { Id = source.Id, Path = source.Path, Kind = source.Kind, MediaSignature = source.Signature };
                    try
                    {
                        var info = await service.Send(config, "/Inspect", new { ItemId = disc.Id, MediaSourceId = source.Id }, token);
                        entry.State = MediaFeatureCatalog.Text(info, "state"); entry.Signature = MediaFeatureCatalog.Text(info, "signature"); entry.Count = info.GetProperty("count").GetInt32();
                        entry.Playlist = MediaFeatureCatalog.Text(info, "playlist");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e) { entry.State = "error"; movie.Message = Error(e); log.Add("Warning", "NativeChapters", $"{movie.Name}（ID {movie.Id}）章节状态读取失败：{movie.Message}"); }
                    movie.Sources.Add(entry);
                }
                found.Add(movie.Id, movie);
                if (movie.Sources.All(s => s.State == "managed")) { movie.Result = "completed"; movie.Message = "原生章节已核验，默认无需重复处理。"; }
                else if (Done(movie)) { movie.Result = "skipped"; movie.Message = "已有非等间隔或自定义章节，保留。"; }
                Update(() => { plan.Items.Add(movie); plan.Message = $"正在检查「{group.Name}」：已找到 {plan.Items.Count} 部 BDMV / ISO。"; });
            }
            Update(() => group.ScanComplete = true);
            log.Add("Information", "NativeChapters", $"媒体库「{group.Name}」：{group.MoviesChecked} 部电影，{found.Values.Count(m => m.LibraryIds.Contains(group.Id))} 部原盘；已读取章节状态。");
        }
        Update(() => { plan.ScanComplete = true; plan.Status = "ready"; plan.Message = $"检查完成：{plan.Items.Count} 部原盘，{plan.Items.Count(i => !Done(i))} 部待补全。已有非等间隔章节默认保留。"; });
        log.Add("Information", "NativeChapters", plan.Message);
    }
    private async Task FillCore(HelperSettings config, ChapterMovie[] movies, CancellationToken token)
    {
        var server = await catalog.Read(config, "System/Info/Public", token);
        if (MediaFeatureCatalog.Text(server, "Id") != plan.ServerId) throw new InvalidOperationException("Emby 服务器已变化，请重新检查。");
        var scope = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in plan.SelectedLibraryIds) await catalog.Movies(config, id, i => scope.Add(MediaFeatureCatalog.Text(i, "Id")), token);
        log.Add("Information", "NativeChapters", $"开始补全原盘章节：{string.Join("、", plan.Libraries.Where(l => plan.SelectedLibraryIds.Contains(l.Id)).Select(l => l.Name))}，共 {movies.Length} 部。");
        foreach (var movie in movies)
        {
            token.ThrowIfCancellationRequested();
            if (!settings.Current.EnableNativeChapters) throw new InvalidOperationException("原盘章节已关闭，后续任务停止。");
            Update(() => { movie.Result = "running"; plan.CurrentName = movie.Name; plan.Message = $"[{plan.Processed + 1}/{plan.Planned}] {movie.Name}：读取原生章节。"; });
            var watch = Stopwatch.StartNew();
            try
            {
                if (!scope.Contains(movie.Id)) throw new InvalidOperationException("影片已离开所选媒体库，请重新检查。");
                var current = await catalog.Movie(config, movie.Id, token) ?? throw new InvalidOperationException("影片已删除或不再是原盘。");
                var preserved = 0;
                foreach (var source in movie.Sources.Where(s => plan.IncludeVerified || s.State is not ("managed" or "existing")))
                {
                    var before = current.Sources.SingleOrDefault(s => s.Id == source.Id && s.Path == source.Path && s.Signature == source.MediaSignature);
                    if (before is null) throw new InvalidOperationException("原盘媒体源已变化，请重新检查。");
                    var inspect = await service.Send(config, "/Inspect", new { ItemId = movie.Id, MediaSourceId = source.Id }, token);
                    var signature = MediaFeatureCatalog.Text(inspect, "signature");
                    if (source.Signature.Length > 0 && source.Signature != signature) throw new InvalidOperationException("检查之后章节已变化，请重新检查；未覆盖。");
                    if (MediaFeatureCatalog.Text(inspect, "state") == "existing") { Update(() => source.State = "existing"); preserved++; continue; }
                    var response = await service.Send(config, "/Fill", new { ItemId = movie.Id, MediaSourceId = source.Id, ExpectedSignature = signature }, token);
                    var saved = await service.Send(config, "/Inspect", new { ItemId = movie.Id, MediaSourceId = source.Id }, token);
                    var after = (await catalog.Movie(config, movie.Id, token))?.Sources.SingleOrDefault(s => s.Id == source.Id && s.Path == source.Path);
                    if (response.GetProperty("verified").ValueKind != JsonValueKind.True || after?.Signature != before.Signature ||
                        MediaFeatureCatalog.Text(response, "signature") != MediaFeatureCatalog.Text(saved, "signature") || saved.GetProperty("count").GetInt32() != response.GetProperty("count").GetInt32())
                        throw new InvalidOperationException("保存后的章节或媒体信息未通过读回核验，请重新检查。");
                    Update(() => { source.State = "managed"; source.Count = saved.GetProperty("count").GetInt32(); source.Signature = MediaFeatureCatalog.Text(saved, "signature"); source.Playlist = MediaFeatureCatalog.Text(response, "playlist"); });
                    log.Add("Information", "NativeChapters", $"{movie.Name}（ID {movie.Id}），{source.Kind}，播放列表 {source.Playlist}：{source.Count} 个原生章节，{watch.Elapsed.TotalSeconds:F2} 秒；时间点（秒）：{response.GetProperty("seconds")}。音轨、字幕、时长已核对。");
                }
                Finish(movie, preserved == movie.Sources.Count ? "skipped" : "completed", preserved > 0 ? "已有章节已保留；可补全部分已处理。" : "原生章节已保存并核验。 ");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { Finish(movie, "failed", Error(e)); }
        }
        Update(() => { plan.Status = "completed"; plan.CurrentName = ""; plan.Message = $"章节任务结束：成功 {plan.Completed}，失败 {plan.Failed}，保留 / 跳过 {plan.Skipped}。"; });
        log.Add(plan.Failed > 0 ? "Warning" : "Information", "NativeChapters", plan.Message);
    }
    private void Finish(ChapterMovie movie, string result, string message)
    { Update(() => { movie.Result = result; movie.Message = message; plan.Processed++; if (result == "completed") plan.Completed++; else if (result == "failed") plan.Failed++; else plan.Skipped++; });
        log.Add(result == "failed" ? "Warning" : "Information", "NativeChapters", $"[{plan.Processed}/{plan.Planned}] {movie.Name}（ID {movie.Id}）：{message}"); }
    private static string Error(Exception e) => e is OperationCanceledException ? "章节读取超时，可继续重试。" : HelperLog.Redact(e.Message);
    private void Update(Action action) { lock (gate) { action(); Persist(); } }
    private void Persist()
    {
        try { var file = FilePath + ".new"; File.WriteAllText(file, JsonSerializer.Serialize(plan, SettingsStore.Json)); SettingsStore.Restrict(file); File.Move(file, FilePath, true); plan.PersistenceWarning = null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { plan.PersistenceWarning = "无法保存章节任务记录，请检查数据目录权限和可用空间。"; log.Add("Warning", "NativeChapters", plan.PersistenceWarning); }
    }
    public async ValueTask DisposeAsync() { shutdown.Cancel(); if (worker is not null) await worker; cancellation?.Dispose(); shutdown.Dispose(); }
}

internal sealed class ChapterPlan
{
    public string Id { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string Connection { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string Message { get; set; } = "先检查媒体库，再补全原生章节。";
    public string? PersistenceWarning { get; set; }
    public bool ScanComplete { get; set; }
    public bool IncludeVerified { get; set; }
    public int Processed { get; set; }
    public int Planned { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string CurrentName { get; set; } = "";
    public List<string> SelectedLibraryIds { get; set; } = [];
    public List<FeatureLibrary> Libraries { get; set; } = [];
    public List<ChapterMovie> Items { get; set; } = [];
}
internal sealed class ChapterMovie
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Result { get; set; } = "pending";
    public string Message { get; set; } = "";
    public List<string> LibraryIds { get; set; } = [];
    public List<ChapterSource> Sources { get; set; } = [];
}
internal sealed class ChapterSource
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string MediaSignature { get; set; } = "";
    public string Signature { get; set; } = "";
    public string State { get; set; } = "empty";
    public int Count { get; set; }
    public string Playlist { get; set; } = "";
}
