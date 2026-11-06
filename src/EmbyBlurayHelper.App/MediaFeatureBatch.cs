using System.Text.Json;

namespace EmbyBlurayHelper.App;

// Persistent batch workflow; format rules and read-back checks live in the catalog.
internal sealed class MediaFeatureBatch : IAsyncDisposable
{
    private readonly SettingsStore settings;
    private readonly MediaFeatureCatalog catalog;
    private readonly Func<HelperSettings, string, string, CancellationToken, Task<JsonElement>> probe;
    private readonly HelperLog log;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private MediaFeaturePlan plan = new();
    private string FilePath => Path.Combine(settings.DataDirectory, "media-feature-task.json");
    private static bool Active(string status) => status is "scanning" or "filling" or "cancelling";
    // Read by LibraryPipeline to chain fills without parsing the snapshot.
    internal (bool Active, string Status, string Message) State { get { lock (gate) return (Active(plan.Status), plan.Status, plan.Message); } }
    private static bool Verified(FeatureMovie movie) => movie.Sources.Count > 0 && movie.Sources.All(s => s.VerifiedAt.HasValue);

    internal MediaFeatureBatch(SettingsStore settings, HttpClient client, HelperLog log,
        Func<HelperSettings, string, string, CancellationToken, Task<JsonElement>> probe)
    {
        this.settings = settings; this.log = log; this.probe = probe;
        catalog = new MediaFeatureCatalog(settings, client);
        if (!File.Exists(FilePath)) return;
        try
        {
            plan = JsonSerializer.Deserialize<MediaFeaturePlan>(File.ReadAllText(FilePath), SettingsStore.Json) ?? throw new JsonException();
            if (Active(plan.Status))
            {
                plan.Status = "interrupted"; plan.FinishedAt = DateTimeOffset.UtcNow;
                plan.Message = "上次任务中断。可继续补充；当前已提交的 Emby 探测可能仍会完成。";
                foreach (var movie in plan.Items.Where(i => i.Result == "running")) movie.Result = "unverified";
                Persist();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { plan = new MediaFeaturePlan { Message = "上次规格任务记录无法读取，请重新检查媒体库。" }; log.Add("Warning", "MediaFeatureBatch", plan.Message); }
    }
    internal object Snapshot()
    {
        lock (gate)
        {
            var same = plan.ConnectionFingerprint == MediaFeatureCatalog.Connection(settings.Current) && plan.EngineSignature == MediaFeatureCatalog.Engine(settings.Current);
            var items = same ? plan.Items : [];
            var libraries = same ? plan.Libraries : [];
            var run = plan.PlannedItemIds.ToHashSet(StringComparer.Ordinal);
            return new
            {
                plan.Id, plan.Status, plan.Operation, message = same ? plan.Message : "Emby 连接、全局范围或规格配置已变化，请重新检查。", plan.PersistenceWarning, scanComplete = same && plan.ScanComplete,
                plan.StartedAt, plan.FinishedAt, moviesChecked = same ? plan.MoviesChecked : 0, plan.Planned, plan.Processed, plan.Completed, plan.Failed, plan.Skipped,
                plan.CurrentItemId, plan.CurrentName, plan.SelectedLibraryIds, plan.IncludeVerified,
                active = Active(plan.Status), connectionMatches = same,
                canFill = !Active(plan.Status) && plan.ScanComplete && same && plan.Items.Count > 0,
                summary = new { discs = items.Count, verified = items.Count(Verified), pending = items.Count(i => !Verified(i)),
                    bdmv = items.Count(i => i.Sources.Any(s => s.Kind == "BDMV")), iso = items.Count(i => i.Sources.Any(s => s.Kind == "ISO")) },
                libraries = libraries.Select(l => new
                {
                    l.Id, l.Name, l.ScanComplete, l.MoviesChecked,
                    discs = plan.Items.Count(i => i.LibraryIds.Contains(l.Id)),
                    verified = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && Verified(i)),
                    pending = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && !Verified(i)),
                    taskPlanned = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && run.Contains(i.Id)),
                    taskProcessed = plan.Items.Count(i => i.LibraryIds.Contains(l.Id) && run.Contains(i.Id) && i.ProcessedInRun)
                }).ToArray(),
                items = items.Select(i => new { i.Id, i.Name, i.LibraryIds, i.Result, i.Message, i.AttemptedAt,
                    verified = Verified(i), sources = i.Sources.Select(s => new { s.Id, s.Kind, s.Details, s.VerifiedAt }).ToArray() }).ToArray()
            };
        }
    }
    internal object Scan()
    {
        lock (gate)
        {
            RequireIdle(); var config = Config();
            var history = (plan.ConnectionIdentity.Length > 0 ? plan.ConnectionIdentity : plan.ConnectionFingerprint) == SettingsStore.ConnectionIdentity(config) && plan.EngineSignature == MediaFeatureCatalog.Engine(config)
                ? plan.Items.Concat(plan.VerificationHistory).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) : [];
            plan = new MediaFeaturePlan { Id = Guid.NewGuid().ToString("N"), Status = "scanning", StartedAt = DateTimeOffset.UtcNow,
                ConnectionFingerprint = MediaFeatureCatalog.Connection(config), ConnectionIdentity = SettingsStore.ConnectionIdentity(config), EngineSignature = MediaFeatureCatalog.Engine(config),
                ServerId = plan.ServerId, VerificationHistory = history.Values.ToList(),
                Message = "正在读取媒体库，仅收集 BDMV 文件夹和 Blu-ray ISO，不探测视频。" };
            Launch(token => ScanCore(config, history, token)); return Snapshot();
        }
    }
    internal object Fill(string planId, string[]? libraryIds, bool includeVerified = false)
    {
        lock (gate)
        {
            RequireIdle(); var config = Config();
            if (!plan.ScanComplete || plan.Id != planId || plan.ConnectionFingerprint != MediaFeatureCatalog.Connection(config) ||
                plan.EngineSignature != MediaFeatureCatalog.Engine(config)) throw new InvalidOperationException("计划已失效，请重新检查媒体库。");
            if (!config.EnableMediaFeatures) throw new InvalidOperationException("请先开启上方的原盘音视频规格识别。");
            var selected = (libraryIds ?? plan.Libraries.Select(l => l.Id).ToArray()).Distinct().ToArray();
            if (selected.Length == 0 || selected.Any(id => !plan.Libraries.Any(l => l.Id == id)))
                throw new ArgumentException("请选择本次计划内的媒体库，空选不会处理全部媒体库。");
            var pending = plan.Items.Where(i => i.LibraryIds.Intersect(selected).Any() && (includeVerified || !Verified(i))).ToArray();
            if (pending.Length == 0) throw new InvalidOperationException("所选范围没有待核验原盘。需要重做时可勾选「重新核验已完成」。");
            plan.Status = "filling"; plan.Operation = "fill"; plan.StartedAt = DateTimeOffset.UtcNow; plan.FinishedAt = null;
            plan.SelectedLibraryIds = selected.ToList(); plan.IncludeVerified = includeVerified;
            plan.PlannedItemIds = pending.Select(i => i.Id).ToList(); plan.Planned = pending.Length;
            plan.Processed = plan.Completed = plan.Failed = plan.Skipped = 0;
            plan.CurrentItemId = plan.CurrentName = null;
            foreach (var movie in plan.Items) movie.ProcessedInRun = false;
            foreach (var movie in pending) { movie.Result = "pending"; movie.Message = "等待规格核验"; }
            plan.Message = $"已计划 {pending.Length} 部 BDMV / ISO，正在检查增强探测条件。";
            Launch(token => FillCore(config, pending, token)); return Snapshot();
        }
    }
    internal object Cancel()
    {
        lock (gate)
        {
            if (Active(plan.Status)) { plan.Status = "cancelling"; plan.Message = "正在停止后续提交；已交给 Emby 的探测可能仍会完成。"; cancellation?.Cancel(); Persist(); }
            return Snapshot();
        }
    }
    private void RequireIdle() { if (Active(plan.Status)) throw new InvalidOperationException("已有规格任务在运行，请等待完成或先停止。"); }
    private HelperSettings Config()
    {
        var config = settings.Current;
        if (string.IsNullOrWhiteSpace(config.EmbyApiKey)) throw new InvalidOperationException("请先保存 Emby 地址和 API key。");
        return config;
    }
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
                var message = cancelled ? "任务已停止，可继续补充；已提交给 Emby 的当前探测可能仍会完成。" : Error(e);
                Update(() =>
                {
                    plan.Status = cancelled ? "cancelled" : "failed"; plan.FinishedAt = DateTimeOffset.UtcNow; plan.Message = message;
                    foreach (var movie in plan.Items.Where(i => i.Result == "running")) { movie.Result = "unverified"; movie.Message = message; }
                });
                log.Add(cancelled ? "Warning" : "Error", "MediaFeatureBatch", message);
            }
        });
    }
    private async Task ScanCore(HelperSettings config, Dictionary<string, FeatureMovie> history, CancellationToken token)
    {
        log.Add("Information", "MediaFeatureBatch", "开始检查媒体库中的 BDMV / ISO 规格计划，普通媒体文件不参与。");
        var server = await catalog.Read(config, "System/Info/Public", token);
        var serverId = MediaFeatureCatalog.Text(server, "Id");
        if (serverId.Length == 0) throw new InvalidDataException("Emby 未返回服务器 ID。");
        if (plan.ServerId.Length > 0 && plan.ServerId != serverId) { history.Clear(); Update(() => plan.VerificationHistory.Clear()); }
        var groups = await catalog.Libraries(config, token);
        Update(() => { plan.ServerId = serverId; plan.Libraries = groups; });
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var discs = new Dictionary<string, FeatureMovie>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            await catalog.Movies(config, group.Id, raw =>
            {
                var id = MediaFeatureCatalog.Text(raw, "Id"); visited.Add(id);
                lock (gate)
                {
                    group.MoviesChecked++; plan.MoviesChecked = visited.Count;
                    if (discs.TryGetValue(id, out var shared)) shared.LibraryIds.Add(group.Id);
                    else if (MediaFeatureCatalog.Analyze(raw) is { } movie)
                    {
                        movie.LibraryIds.Add(group.Id);
                        if (history.TryGetValue(id, out var old))
                            foreach (var source in movie.Sources)
                            {
                                var checkedSource = old.Sources.SingleOrDefault(s => s.Id == source.Id && s.Path == source.Path && s.Signature == source.Signature);
                                source.VerifiedAt = checkedSource?.VerifiedAt;
                            }
                        if (Verified(movie)) { movie.Result = "completed"; movie.Message = "上次批量核验结果仍有效"; }
                        discs.Add(id, movie); plan.Items.Add(movie); plan.VerificationHistory.RemoveAll(i => i.Id == id);
                    }
                    plan.Message = $"正在检查「{group.Name}」：{group.MoviesChecked} 部电影，累计 {plan.Items.Count} 部 BDMV / ISO。";
                }
            }, token, () => Update(() => { }));
            Update(() => group.ScanComplete = true);
            log.Add("Information", "MediaFeatureBatch", $"媒体库「{group.Name}」检查完成：{group.MoviesChecked} 部电影，{discs.Values.Count(i => i.LibraryIds.Contains(group.Id))} 部 BDMV / ISO。");
        }
        token.ThrowIfCancellationRequested(); catalog.EnsureContext(config);
        Update(() => { plan.Status = "ready"; plan.ScanComplete = true; plan.FinishedAt = DateTimeOffset.UtcNow;
            plan.Message = $"检查完成：{plan.Items.Count} 部 BDMV / ISO，{plan.Items.Count(Verified)} 部批量已核验，{plan.Items.Count(i => !Verified(i))} 部待核验。"; });
        log.Add("Information", "MediaFeatureBatch", plan.Message);
    }
    private async Task RequireProbe(HelperSettings config, CancellationToken token)
    {
        catalog.EnsureContext(config, true);
        if (!File.Exists(config.FeatureProbePath)) throw new InvalidOperationException("独立增强 ffprobe 不可访问，请检查上方配置。");
        if (MediaFeatureCatalog.Engine(config) != plan.EngineSignature) throw new InvalidOperationException("增强引擎文件已变化，请重新检查媒体库。");
        var options = await catalog.Read(config, "Encoding/FfmpegOptions", token);
        if (options.TryGetProperty("Object", out var inner)) options = inner;
        if (!options.TryGetProperty("UseCustomProbePath", out var enabled) || enabled.ValueKind != JsonValueKind.True ||
            !ProbeSettings.IsOurPath(MediaFeatureCatalog.Text(options, "CustomProbePath")))
            throw new InvalidOperationException("Emby 探测器未接入本工具，请在上方重新保存规格设置。");
    }
    private async Task FillCore(HelperSettings config, FeatureMovie[] pending, CancellationToken token)
    {
        var server = await catalog.Read(config, "System/Info/Public", token);
        if (MediaFeatureCatalog.Text(server, "Id") != plan.ServerId) throw new InvalidOperationException("Emby 服务器已改变，请重新检查媒体库。");
        var plugin = await catalog.Read(config, "BlurayHelper/Status", token);
        if (!plugin.TryGetProperty("capabilities", out var capabilities) || !capabilities.EnumerateArray().Any(c => c.GetString() == "media-features-verification"))
            throw new InvalidOperationException("批量规格核验需要 EmbyBlurayHelper.Plugin 0.1.4 或更新版本，请安装 DLL 并重启 Emby。");
        await RequireProbe(config, token);
        var libraries = await catalog.Libraries(config, token);
        if (plan.SelectedLibraryIds.Any(id => !libraries.Any(l => l.Id == id))) throw new InvalidOperationException("所选媒体库已删除，请重新检查。");
        var scope = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in plan.SelectedLibraryIds)
            await catalog.Movies(config, id, item => scope.Add(MediaFeatureCatalog.Text(item, "Id")), token);
        log.Add("Information", "MediaFeatureBatch", $"开始批量补充规格：{string.Join("、", plan.Libraries.Where(l => plan.SelectedLibraryIds.Contains(l.Id)).Select(l => l.Name))}；{pending.Length} 部原盘，逐个媒体源探测并读回核验。");
        foreach (var movie in pending)
        {
            token.ThrowIfCancellationRequested(); await RequireProbe(config, token);
            Update(() => { plan.CurrentItemId = movie.Id; plan.CurrentName = movie.Name; movie.Result = "running"; movie.AttemptedAt = DateTimeOffset.UtcNow;
                movie.Message = "正在核对媒体源"; plan.Message = $"[{plan.Processed + 1}/{plan.Planned}] {movie.Name}：正在补充原盘规格。"; });
            try
            {
                if (!scope.Contains(movie.Id)) { Finish(movie, "skipped", "影片已不在所选媒体库中，请重新检查"); continue; }
                var before = await catalog.Movie(config, movie.Id, token);
                if (before is null) { Finish(movie, "skipped", "影片已删除或已不是 BDMV / ISO"); continue; }
                if (before.Sources.Count != movie.Sources.Count || movie.Sources.Any(s => !before.Sources.Any(b => b.Id == s.Id && b.Path == s.Path && b.Signature == s.Signature)))
                    throw new InvalidOperationException("原盘媒体源或保存信息已变化，请重新检查媒体库后重试。");
                foreach (var source in movie.Sources.Where(s => plan.IncludeVerified || !s.VerifiedAt.HasValue))
                {
                    token.ThrowIfCancellationRequested(); catalog.EnsureContext(config, true);
                    Update(() => { source.VerifiedAt = null; movie.Message = $"正在识别 {source.Kind} 媒体源 {source.Id}，等待 Emby 返回核验凭据"; });
                    var response = await probe(config, movie.Id, source.Id, token);
                    catalog.EnsureContext(config, true);
                    var afterMovie = await catalog.Movie(config, movie.Id, token);
                    var after = afterMovie?.Sources.SingleOrDefault(s => s.Id == source.Id && s.Path == source.Path);
                    var old = before.Sources.Single(s => s.Id == source.Id);
                    if (after is null || !MediaFeatureCatalog.Preserved(old, after) || !MediaFeatureCatalog.SavedFeatures(after, response))
                        throw new InvalidOperationException("Emby 已返回，但保存的规格或原有信息未通过读回核验；保留为待重试，请检查日志。");
                    Update(() => { source.Signature = after.Signature; source.Details = after.Details; source.VerifiedAt = DateTimeOffset.UtcNow; });
                    log.Add("Information", "MediaFeatureBatch", $"{movie.Name}（ID {movie.Id}），{source.Kind} 媒体源 {source.Id}：核验完成；{source.Details}。原有语言、时长和轨道信息已核对。");
                }
                Finish(movie, "completed", "全部原盘媒体源已核验并保存；未确认新增标记的结果也已记录");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { Finish(movie, "failed", Error(e)); }
        }
        Update(() => { plan.Status = "completed"; plan.FinishedAt = DateTimeOffset.UtcNow; plan.CurrentItemId = plan.CurrentName = null;
            plan.Message = $"规格任务结束：已核验 {plan.Completed} 部，失败 {plan.Failed} 部，跳过 {plan.Skipped} 部。失败和未核验媒体源可继续处理。"; });
        log.Add(plan.Failed > 0 ? "Warning" : "Information", "MediaFeatureBatch", plan.Message);
    }
    private void Finish(FeatureMovie movie, string result, string message)
    {
        Update(() => { movie.Result = result; movie.Message = message; movie.ProcessedInRun = true; plan.Processed++;
            if (result == "completed") plan.Completed++; else if (result == "failed") plan.Failed++; else plan.Skipped++; });
        log.Add(result == "failed" ? "Warning" : "Information", "MediaFeatureBatch", $"[{plan.Processed}/{plan.Planned}] {movie.Name}（ID {movie.Id}）：{message}");
    }
    private static string Error(Exception e) => e is OperationCanceledException ? "规格探测超时，未计为核验完成；可继续重试。"
        : HelperLog.Redact(e.Message);
    private void Update(Action action) { lock (gate) { action(); Persist(); } }
    private void Persist()
    {
        try { var temporary = FilePath + ".new"; File.WriteAllText(temporary, JsonSerializer.Serialize(plan, SettingsStore.Json));
            SettingsStore.Restrict(temporary); File.Move(temporary, FilePath, true); plan.PersistenceWarning = null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (plan.PersistenceWarning is null) log.Add("Error", "MediaFeatureBatch", "无法保存规格任务进度，请检查数据目录权限和可用空间。");
            plan.PersistenceWarning = "无法保存任务记录，重启后可能丢失进度。";
        }
    }
    public async ValueTask DisposeAsync()
    { shutdown.Cancel(); if (worker is not null) await worker; cancellation?.Dispose(); shutdown.Dispose(); }
}
