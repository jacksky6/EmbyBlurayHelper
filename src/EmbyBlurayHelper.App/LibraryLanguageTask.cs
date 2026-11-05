using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyBlurayHelper.App;

// Schedules and verifies work. Classification and persistent data models live in their own modules.
internal sealed class LibraryLanguageTask : IAsyncDisposable
{
    private readonly SettingsStore settings;
    private readonly HttpClient client;
    private readonly HelperLog log;
    private readonly IsoPrerequisites isoPrerequisites;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private LibraryPlan plan = new();
    private readonly TimeSpan verificationTimeout;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan mediaReadTimeout;
    private string FilePath => Path.Combine(settings.DataDirectory, "library-language-task.json");
    private static bool Active(string status) => status is "scanning" or "filling" or "cancelling";

    public LibraryLanguageTask(SettingsStore settings, HttpClient client, HelperLog log,
        TimeSpan? verificationTimeout = null, TimeSpan? pollInterval = null, IsoPrerequisites? isoPrerequisites = null,
        TimeSpan? mediaReadTimeout = null)
    {
        this.settings = settings;
        this.client = client;
        this.log = log;
        this.isoPrerequisites = isoPrerequisites ?? new IsoPrerequisites(client);
        this.verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(15);
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        // Do not inherit the five-minute client timeout for a media-info read.
        // A damaged or busy disc must not stall an entire batch task.
        this.mediaReadTimeout = mediaReadTimeout ?? TimeSpan.FromSeconds(5);
        if (!File.Exists(FilePath)) return;
        try
        {
            plan = JsonSerializer.Deserialize<LibraryPlan>(File.ReadAllText(FilePath), SettingsStore.Json)
                ?? throw new JsonException("任务记录为空。");
            if (Active(plan.Status))
            {
                plan.Status = "interrupted";
                plan.FinishedAt = DateTimeOffset.UtcNow;
                plan.Message = "工具在任务期间退出。可重新检查或继续补全；已提交给 Emby 的探测可能仍会执行。";
                foreach (var movie in plan.Items.Where(i => i.Result == "running")) movie.Result = "unverified";
                Persist();
                log.Add("Warning", "LibraryLanguage", plan.Message);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            plan = new LibraryPlan { Message = "上次任务记录无法读取，请重新检查媒体库。" };
            log.Add("Warning", "LibraryLanguage", plan.Message);
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            var same = plan.ConnectionFingerprint == Fingerprint(settings.Current);
            var pending = plan.Items.Count(i => i.Classification is "missing" or "unknown");
            var plannedIds = plan.PlannedItemIds.ToHashSet(StringComparer.Ordinal);
            return new
            {
                plan.Id, plan.Status, plan.Operation, plan.StartedAt, plan.FinishedAt, plan.CheckedAt,
                plan.ScanComplete, plan.MoviesChecked, plan.MoviesTotal, plan.Planned, plan.Processed,
                plan.Completed, plan.Partial, plan.Failed, plan.Skipped, plan.CurrentItemId, plan.CurrentName,
                plan.Message, plan.PersistenceWarning, plan.SelectedLibraryIds, plan.Scope,
                librariesChecked = plan.Libraries.Count(l => l.ScanComplete),
                active = Active(plan.Status), connectionMatches = same,
                canFill = !Active(plan.Status) && plan.ScanComplete && plan.Libraries.Count > 0 && same && pending > 0,
                summary = new
                {
                    blurayMovies = plan.Items.Count,
                    uhdMovies = plan.Items.Count(i => i.Format == "UHD"),
                    completeMovies = plan.Items.Count(i => i.Classification == "complete"),
                    missingMovies = plan.Items.Count(i => i.Classification == "missing"),
                    unknownMovies = plan.Items.Count(i => i.Classification == "unknown"),
                    unsupportedMovies = plan.Items.Count(i => i.Classification == "unsupported"),
                    missingAudio = plan.Items.Sum(i => i.MissingAudio),
                    missingSubtitles = plan.Items.Sum(i => i.MissingSubtitles),
                    missingRuntimeMovies = plan.Items.Count(i => i.MissingRuntime > 0), pendingMovies = pending
                },
                libraries = plan.Libraries.Select(l =>
                {
                    var movies = plan.Items.Where(i => i.LibraryIds.Contains(l.Id)).ToArray();
                    var run = movies.Where(i => plannedIds.Contains(i.Id)).ToArray();
                    return new
                    {
                        l.Id, l.Name, l.MoviesChecked, l.MoviesTotal, l.ScanComplete,
                        blurayMovies = movies.Length,
                        completeMovies = movies.Count(i => i.Classification == "complete"),
                        missingMovies = movies.Count(i => i.Classification == "missing"),
                        unknownMovies = movies.Count(i => i.Classification == "unknown"),
                        unsupportedMovies = movies.Count(i => i.Classification == "unsupported"),
                        pendingMovies = movies.Count(i => i.Classification is "missing" or "unknown"),
                        missingAudio = movies.Sum(i => i.MissingAudio), missingSubtitles = movies.Sum(i => i.MissingSubtitles),
                        missingRuntimeMovies = movies.Count(i => i.MissingRuntime > 0),
                        taskPlanned = run.Length, taskProcessed = run.Count(i => i.ProcessedInRun)
                    };
                }).ToArray(),
                items = plan.Items.Select(i => new
                {
                    i.Id, i.Name, i.Format, i.Classification, i.MissingAudio, i.MissingSubtitles, i.MissingRuntime, i.LibraryIds,
                    sources = i.Sources.Select(s => new { s.Id, s.IsIso, s.RunTimeTicks }).ToArray(),
                    i.Result, i.Message, i.AttemptedAt, i.SubmittedAt, i.VerifiedAt, i.NeedsPlaylistRepair
                }).ToArray()
            };
        }
    }

    public object Scan()
    {
        lock (gate)
        {
            RequireIdle();
            var config = Config();
            var history = plan.ConnectionFingerprint == Fingerprint(config) ? plan.Items.ToDictionary(i => i.Id) : [];
            plan = new LibraryPlan
            {
                Id = Guid.NewGuid().ToString("N"), Status = "scanning", Operation = "scan",
                StartedAt = DateTimeOffset.UtcNow, ConnectionFingerprint = Fingerprint(config),
                Message = "正在读取 Emby 电影库和已保存的音轨、字幕语言及原盘时长。"
            };
            Launch(token => ScanCore(config, history, token));
            return Snapshot();
        }
    }

    public object Fill(string planId, string[]? libraryIds = null)
    {
        lock (gate)
        {
            RequireIdle();
            var config = Config();
            if (plan.Id != planId || !plan.ScanComplete) throw new InvalidOperationException("检查计划已变化或尚未完成，请重新检查媒体库。");
            if (plan.ConnectionFingerprint != Fingerprint(config)) throw new InvalidOperationException("Emby 连接已变更，请重新检查媒体库。");
            if (!config.EnableProbeLanguagePatch) throw new InvalidOperationException("请先在「语言与时长」开启「Emby 探测语言补全」并确认已接入。");
            var selected = (libraryIds ?? plan.Libraries.Select(l => l.Id).ToArray()).Distinct(StringComparer.Ordinal).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("请至少选择一个媒体库。");
            if (selected.Any(id => !plan.Libraries.Any(l => l.Id == id))) throw new InvalidOperationException("所选媒体库不在检查计划中，请重新检查媒体库。");
            var pending = plan.Items.Where(i => i.LibraryIds.Intersect(selected).Any() && i.Classification is "missing" or "unknown").ToArray();
            if (pending.Length == 0) throw new InvalidOperationException("所选媒体库没有需要补全的原盘。");
            plan.SelectedLibraryIds = selected.ToList();
            plan.Scope = selected.Length == plan.Libraries.Count ? "全部媒体库" : string.Join("、", plan.Libraries.Where(l => selected.Contains(l.Id)).Select(l => l.Name));
            plan.PlannedItemIds = pending.Select(i => i.Id).ToList();
            plan.Status = "filling"; plan.Operation = "fill";
            plan.StartedAt = DateTimeOffset.UtcNow; plan.FinishedAt = null;
            plan.Planned = pending.Length;
            plan.Processed = plan.Completed = plan.Partial = plan.Failed = plan.Skipped = 0;
            plan.CurrentItemId = plan.CurrentName = null;
            plan.Message = $"范围：{plan.Scope}。已计划 {pending.Length} 部原盘，正在核对探测器接入状态。";
            foreach (var movie in plan.Items) movie.ProcessedInRun = false;
            foreach (var movie in pending) { movie.Result = "pending"; movie.Message = "等待补全"; }
            Launch(token => FillCore(config, pending, token));
            return Snapshot();
        }
    }

    public object Cancel()
    {
        lock (gate)
        {
            if (Active(plan.Status))
            {
                plan.Status = "cancelling";
                plan.Message = "正在停止本工具的任务；已提交给 Emby 的当前探测可能仍会完成。";
                cancellation?.Cancel(); Persist();
            }
            return Snapshot();
        }
    }

    private void RequireIdle()
    {
        if (Active(plan.Status)) throw new InvalidOperationException("已有检查或补全任务正在运行，请等待完成或先停止。");
    }

    private HelperSettings Config()
    {
        var config = settings.Current;
        if (string.IsNullOrWhiteSpace(config.EmbyApiKey)) throw new InvalidOperationException("请先保存 Emby 地址和 API key。");
        return config;
    }

    private static string Fingerprint(HelperSettings config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.EmbyUrl.TrimEnd('/') + "\n" + config.EmbyApiKey)));

    private void EnsureContext(HelperSettings config, bool filling = false)
    {
        var current = settings.Current;
        if (Fingerprint(current) != Fingerprint(config) || (filling &&
            (!current.EnableProbeLanguagePatch || current.UpstreamProbePath != config.UpstreamProbePath)))
            throw new InvalidOperationException("Emby 连接或探测器配置已变更，任务已停止。请检查设置后重新检查媒体库。");
    }

    private void Launch(Func<CancellationToken, Task> action)
    {
        cancellation?.Dispose();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var token = cancellation.Token;
        Persist();
        worker = Task.Run(async () =>
        {
            try { await action(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Update(() =>
                {
                    plan.Status = "cancelled"; plan.FinishedAt = DateTimeOffset.UtcNow;
                    plan.Message = "任务已停止。可继续补全；已提交给 Emby 的探测可能仍会执行。";
                    foreach (var movie in plan.Items.Where(i => i.Result == "running"))
                    { movie.Result = "unverified"; movie.Message = "停止等待，尚未核验结果"; }
                });
                log.Add("Warning", "LibraryLanguage", $"任务已停止：已处理 {plan.Processed}/{plan.Planned} 部；已提交的 Emby 探测不会被强制中断。");
            }
            catch (Exception e)
            {
                var message = Error(e);
                Update(() =>
                {
                    plan.Status = "failed"; plan.FinishedAt = DateTimeOffset.UtcNow; plan.Message = message;
                    foreach (var movie in plan.Items.Where(i => i.Result == "running"))
                    { movie.Result = "unverified"; movie.Message = message; }
                });
                log.Add("Error", "LibraryLanguage", "任务失败：" + message);
            }
        });
    }

    private async Task ScanCore(HelperSettings config, Dictionary<string, LibraryMovie> history, CancellationToken token)
    {
        log.Add("Information", "LibraryLanguage", "开始检查电影库：读取 Emby 已保存的媒体信息，不探测视频文件。");
        var server = await Read(config, "System/Info/Public", token);
        var serverId = Text(server, "Id");
        if (serverId.Length == 0) throw new InvalidOperationException("Emby 未返回服务器 ID，无法建立可靠的检查计划。");
        Update(() => plan.ServerId = serverId);
        var folders = await Read(config, "Library/VirtualFolders", token);
        if (folders.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Emby 未返回媒体库列表，请检查管理权限。");
        var groups = folders.EnumerateArray().Select(l => new LibraryGroup { Id = Text(l, "ItemId"), Name = Text(l, "Name") }).ToArray();
        if (groups.Any(l => l.Id.Length == 0) || groups.Select(l => l.Id).Distinct().Count() != groups.Length)
            throw new InvalidOperationException("Emby 媒体库 ID 缺失或重复，无法建立可靠的分库计划。");
        Update(() => plan.Libraries = groups.ToList());
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, LibraryMovie>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
          var offset = 0;
          var groupVisited = new HashSet<string>(StringComparer.Ordinal);
          while (true)
          {
            token.ThrowIfCancellationRequested(); EnsureContext(config);
            var page = await Read(config, $"Items?ParentId={Uri.EscapeDataString(group.Id)}&Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams,Path&SortBy=SortName&SortOrder=Ascending&StartIndex={offset}&Limit=200", token);
            if (!page.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Emby 未返回电影列表，请检查 API key 的媒体库读取权限。");
            var total = Number(page, "TotalRecordCount");
            var added = 0;
            var discs = new List<LibraryMovie>();
            var memberships = new List<LibraryMovie>();
            foreach (var item in items.EnumerateArray())
            {
                var id = Text(item, "Id");
                if (id.Length == 0) throw new InvalidOperationException("Emby 返回了没有 ID 的电影，检查结果不完整。");
                if (!groupVisited.Add(id)) continue;
                visited.Add(id);
                added++;
                if (byId.TryGetValue(id, out var existing))
                { memberships.Add(existing); continue; }
                if (Analyze(item) is { } disc)
                {
                    disc.LibraryIds.Add(group.Id);
                    if (history.TryGetValue(disc.Id, out var old) && SameSources(old, disc))
                    {
                        disc.AttemptedAt = old.AttemptedAt; disc.SubmittedAt = old.SubmittedAt; disc.VerifiedAt = old.VerifiedAt;
                        disc.IsoReaderRevision = old.IsoReaderRevision;
                        disc.NeedsPlaylistRepair = old.NeedsPlaylistRepair || (old.VerifiedAt is not null &&
                            old.IsoReaderRevision < 1 && disc.Sources.Any(s => s.IsIso));
                        if (disc.NeedsPlaylistRepair)
                        {
                            disc.Classification = "unknown";
                            disc.Message = "旧版 ISO 播放列表需重新核验；补全时会重读正片轨道及时长，保留外部字幕。";
                        }
                        else if (old.VerifiedAt is not null && disc.Classification == "complete")
                        { disc.Result = "completed"; disc.Message = "上次已由本工具核验，当前语言及时长仍完整"; }
                    }
                    log.Add("Information", "LibraryLanguage",
                        $"[{group.Name}] {disc.Name}（ID {disc.Id}）扫描轨道：{DescribeTracks(disc)}");
                    discs.Add(disc);
                    byId.Add(id, disc);
                }
            }
            Update(() =>
            {
                foreach (var movie in memberships) movie.LibraryIds.Add(group.Id);
                plan.Items.AddRange(discs); plan.MoviesChecked = visited.Count;
                group.MoviesChecked = groupVisited.Count; group.MoviesTotal = Math.Max(total, groupVisited.Count);
                plan.MoviesTotal = Math.Max(visited.Count, plan.Libraries.Sum(l => l.MoviesTotal));
                plan.Message = $"正在检查媒体库「{group.Name}」：{group.MoviesChecked}/{group.MoviesTotal} 部电影。累计找到 {plan.Items.Count} 部蓝光原盘。";
            });
            if (items.GetArrayLength() == 0)
            {
                if (offset < total) throw new InvalidOperationException("Emby 分页提前结束，检查结果不完整，请重新检查。");
                break;
            }
            if (added == 0) throw new InvalidOperationException("Emby 分页没有前进，检查结果不完整，请重新检查。");
            offset += items.GetArrayLength();
            if (total > 0 && offset >= total) break;
          }
          Update(() => group.ScanComplete = true);
          log.Add("Information", "LibraryLanguage", $"媒体库「{group.Name}」检查完成：{group.MoviesChecked} 部电影，{plan.Items.Count(i => i.LibraryIds.Contains(group.Id))} 部蓝光原盘。");
        }
        EnsureContext(config); token.ThrowIfCancellationRequested();
        Update(() =>
        {
            plan.ScanComplete = true; plan.Status = "ready";
            plan.MoviesTotal = plan.MoviesChecked;
            plan.CheckedAt = plan.FinishedAt = DateTimeOffset.UtcNow;
            plan.Message = $"检查完成：{groups.Length} 个媒体库、{plan.MoviesChecked} 部电影中有 {plan.Items.Count} 部蓝光原盘，{plan.Items.Count(i => i.Classification == "missing")} 部语言缺失，{plan.Items.Count(i => i.Classification == "unknown")} 部信息不足。";
        });
        log.Add("Information", "LibraryLanguage", plan.Message);
    }

    private async Task RequireProbe(HelperSettings config, CancellationToken token)
    {
        EnsureContext(config, true);
        var options = await Read(config, "Encoding/FfmpegOptions", token);
        if (options.TryGetProperty("Object", out var value)) options = value;
        if (!Flag(options, "UseCustomProbePath") || !ProbeSettings.IsOurPath(Text(options, "CustomProbePath")))
            throw new InvalidOperationException("Emby 尚未接入本工具的探测器。请先在「语言与时长」开启「Emby 探测语言补全」并确认已接入，再运行补全。");
    }

    public Task<IsoPrerequisiteStatus> CheckIsoPrerequisites(CancellationToken token) => isoPrerequisites.Check(settings.Current, token);

    private async Task FillCore(HelperSettings config, LibraryMovie[] pending, CancellationToken token)
    {
        var server = await Read(config, "System/Info/Public", token);
        if (Text(server, "Id") != plan.ServerId) throw new InvalidOperationException("Emby 服务器已改变，请重新检查媒体库。");
        await RequireProbe(config, token);
        var currentScope = await ScopeItems(config, plan.SelectedLibraryIds, token);
        var isoStatus = await isoPrerequisites.Check(config, token);
        var blockedIso = isoStatus is { CanProbe: false } ? pending.Count(movie => movie.Sources.Any(source => source.IsIso)) : 0;
        if (blockedIso > 0)
            log.Add("Warning", "LibraryLanguage", $"ISO 前置检查未通过，{blockedIso} 部 ISO 不提交探测；仅处理其余 BDMV。{isoStatus!.Message}");
        log.Add("Information", "LibraryLanguage", $"开始批量补全，范围：{plan.Scope}。计划 {pending.Length} 部蓝光原盘，逐部提交并核验音轨、字幕语言及时长。刷新后的核验窗口最多 15 秒，插件实际探测耗时另计。");
        foreach (var movie in pending)
        {
            token.ThrowIfCancellationRequested();
            // Stop the entire job if its connection or probe changes, rather than flooding a new server.
            await RequireProbe(config, token);
            Update(() =>
            {
                plan.CurrentItemId = movie.Id; plan.CurrentName = movie.Name;
                movie.Result = "running"; movie.AttemptedAt = DateTimeOffset.UtcNow;
                movie.Message = "正在核对最新媒体信息";
                plan.Message = $"范围：{plan.Scope}。正在处理 {plan.Processed + 1}/{plan.Planned}：{movie.Name}";
            });
            try
            {
                if (movie.Sources.Any(source => source.IsIso) && isoStatus is { CanProbe: false })
                {
                    FinishMovie(movie, null, "skipped", "ISO 未提交：" + isoStatus.Message);
                    continue;
                }
                if (!currentScope.Contains(movie.Id)) { FinishMovie(movie, null, "skipped", "影片已不在本次选定的媒体库中，请重新检查媒体库"); continue; }
                var before = await ReadMovie(config, movie.Id, token, mediaReadTimeout);
                if (before is null) { FinishMovie(movie, null, "skipped", "影片已删除或已不是蓝光原盘"); continue; }
                if (!SameSources(movie, before)) { FinishMovie(movie, null, "failed", "原盘媒体源已变化，请重新检查媒体库后重试"); continue; }
                if (before.Classification == "complete" && !movie.NeedsPlaylistRepair) { FinishMovie(movie, before, "skipped", "最新语言及时长已完整，无需重新探测"); continue; }
                if (before.Classification == "unsupported") { FinishMovie(movie, before, "skipped", before.Message); continue; }
                log.Add("Information", "LibraryLanguage",
                    $"{movie.Name}（ID {movie.Id}）重新探测前：{DescribeTracks(before)}");
                EnsureContext(config, true);
                if (movie.NeedsPlaylistRepair && !isoStatus.PlaylistRepair)
                { FinishMovie(movie, null, "skipped", "ISO 播放列表修复需要随包附带的 0.1.2 或更新版 DLL；请更新并重启 Emby 后重试。"); continue; }
                var usePlugin = before.Sources.Any(source => source.IsIso) || (isoStatus.CanProbe && isoStatus.BdmvDirectProbe);
                if (usePlugin)
                {
                    if (before.Sources.Any(source => string.IsNullOrWhiteSpace(source.Id)))
                    {
                        FinishMovie(movie, before, "failed", "Emby 未返回完整的蓝光媒体源 ID，未提交探测。");
                        continue;
                    }
                    foreach (var probeSource in before.Sources.Where(source => (movie.NeedsPlaylistRepair && source.IsIso) || source.RunTimeTicks is null or <= 0 || !source.Tracks.Any(track => track.Type == "Audio") ||
                                 source.Tracks.Any(track => Unknown(track.Language))))
                    {
                        EnsureContext(config, true);
                        Update(() => movie.Message = $"正在通过 Emby 插件探测媒体源 {probeSource.Id} 并保存语言、时长。");
                        using var response = await Send(config, HttpMethod.Post, "BlurayHelper/Probe", token,
                            new { ItemId = movie.Id, MediaSourceId = probeSource.Id, RepairPlaylist = movie.NeedsPlaylistRepair && probeSource.IsIso });
                        response.EnsureSuccessStatusCode();
                        var probe = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                        if (probe.ValueKind != JsonValueKind.Object || Text(probe, "itemId") != movie.Id ||
                            Text(probe, "sourceId") != probeSource.Id || Number(probe, "streamCount") <= 0)
                            throw new JsonException("EmbyBlurayHelper 未返回与请求媒体源匹配的有效流列表。");
                        log.Add("Information", "LibraryLanguage",
                            $"[{plan.Processed + 1}/{plan.Planned}] {movie.Name}（ID {movie.Id}）：Emby 已探测媒体源 {probeSource.Id}，返回 {Number(probe, "streamCount")} 条流；音轨 [{ProbeLanguages(probe, "audio")}]；字幕 [{ProbeLanguages(probe, "subtitles")}]；时长 {Runtime(probe, "runTimeTicks")}。");
                    }
                    Update(() => movie.SubmittedAt = DateTimeOffset.UtcNow);
                }
                else if (movie.SubmittedAt is null || movie.VerifiedAt >= movie.SubmittedAt || movie.SubmittedAt.Value.Add(verificationTimeout) <= DateTimeOffset.UtcNow)
                {
                    Update(() => movie.Message = "正在请求 Emby 重新探测 BDMV 文件夹原盘。");
                    using var response = await Send(config, HttpMethod.Post,
                        $"Items/{Uri.EscapeDataString(movie.Id)}/Refresh?Recursive=false&MetadataRefreshMode=FullRefresh&ImageRefreshMode=ValidationOnly&ReplaceAllMetadata=false&ReplaceAllImages=false",
                        token, new { replaceThumbnailImages = false });
                    response.EnsureSuccessStatusCode();
                    Update(() => movie.SubmittedAt = DateTimeOffset.UtcNow);
                    log.Add("Information", "LibraryLanguage",
                        $"[{plan.Processed + 1}/{plan.Planned}] {movie.Name}（ID {movie.Id}）：BDMV 重新探测已提交，等待 Emby 保存媒体信息。");
                }
                else log.Add("Information", "LibraryLanguage", $"{movie.Name}（ID {movie.Id}）：继续核验上次已提交的探测，暂不重复提交。");
                Update(() => movie.Message = "Emby 探测已返回，正在验证已保存的音轨、字幕语言及时长。");
                var deadline = movie.SubmittedAt!.Value.Add(verificationTimeout);
                LibraryMovie? latest = null;
                var mediaReadTimedOut = false;
                do
                {
                    EnsureContext(config, true);
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    try
                    {
                        latest = await ReadMovie(config, movie.Id, token,
                            remaining < mediaReadTimeout ? remaining : mediaReadTimeout);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        mediaReadTimedOut = true;
                        log.Add("Warning", "LibraryLanguage",
                            $"{movie.Name}（ID {movie.Id}）：Emby 媒体信息读回超过 {mediaReadTimeout.TotalSeconds:0.#} 秒，停止本次核验等待，未确认探测结果是否已保存。");
                        break;
                    }
                    if (latest is null || !SameSources(before, latest)) break;
                    if (Verified(before, latest) || usePlugin) break;
                    await Task.Delay(pollInterval, token);
                } while (DateTimeOffset.UtcNow < deadline);
                EnsureContext(config, true);
                if (latest is not null && SameSources(before, latest) && (Verified(before, latest) ||
                    (movie.NeedsPlaylistRepair && latest.Classification == "complete")))
                {
                    if (usePlugin && before.Sources.Any(s => s.IsIso))
                        Update(() => { movie.IsoReaderRevision = 1; movie.NeedsPlaylistRepair = false; });
                    FinishMovie(movie, latest, "completed", "已核验：原有内部音轨、字幕均有语言，各原盘媒体源均有时长");
                }
                else if (latest is not null && SameSources(before, latest) && ContainsTracks(before, latest))
                    FinishMovie(movie, latest, "partial", latest.Classification == "unknown" ? $"探测后仍有信息不足：{latest.MissingRuntime} 个媒体源缺少时长；请查看探测日志核对轨道信息。"
                        : $"{(usePlugin ? "探测已完成" : mediaReadTimedOut ? "读回超时，未确认保存结果" : "等待已结束")}，仍缺少 {latest.MissingAudio} 条音轨、{latest.MissingSubtitles} 条字幕语言；原盘可能标为 und 或未标注，本工具不会猜测语言。请核对原盘探测日志。");
                else if (mediaReadTimedOut && latest is null)
                    FinishMovie(movie, before, "partial", "读回 Emby 媒体信息超时，未确认本次探测是否保存；已保留扫描结果，稍后可重新补全。");
                else
                    FinishMovie(movie, null, "unverified", "无法核验原有媒体源和轨道，未计入成功。请重新检查媒体库。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
            { FinishMovie(movie, null, "failed", Error(e)); }
        }
        Update(() =>
        {
            plan.Status = "completed"; plan.FinishedAt = DateTimeOffset.UtcNow;
            plan.CurrentItemId = plan.CurrentName = null;
            plan.Message = $"范围：{plan.Scope}。补全任务结束：已核验成功 {plan.Completed} 部，仍有缺失 {plan.Partial} 部，失败或未核验 {plan.Failed} 部，跳过 {plan.Skipped} 部。";
            if (blockedIso > 0) plan.Message += $"其中 {blockedIso} 部 ISO 因前置条件未满足而未提交探测。{isoStatus!.Message}";
        });
        log.Add(plan.Failed + plan.Partial + blockedIso > 0 ? "Warning" : "Information", "LibraryLanguage", plan.Message);
    }

    private void FinishMovie(LibraryMovie movie, LibraryMovie? latest, string result, string message)
    {
        Update(() =>
        {
            if (latest is not null)
            {
                movie.Classification = latest.Classification; movie.Sources = latest.Sources;
                movie.MissingAudio = latest.MissingAudio; movie.MissingSubtitles = latest.MissingSubtitles;
                movie.MissingRuntime = latest.MissingRuntime;
            }
            movie.Result = result; movie.Message = message;
            movie.ProcessedInRun = true;
            if (result == "completed" || (result == "skipped" && latest?.Classification == "complete")) movie.VerifiedAt = DateTimeOffset.UtcNow;
            plan.Processed++;
            if (result == "completed") plan.Completed++;
            else if (result == "partial") plan.Partial++;
            else if (result == "skipped") plan.Skipped++;
            else plan.Failed++;
        });
        if (latest is not null)
        {
            log.Add("Information", "LibraryLanguage",
                $"{movie.Name}（ID {movie.Id}）Emby 当前轨道语言：{DescribeTracks(latest)}");
        }
        var libraries = string.Join("、", plan.Libraries.Where(l => movie.LibraryIds.Contains(l.Id)).Select(l => l.Name));
        log.Add(result is "completed" or "skipped" ? "Information" : "Warning", "LibraryLanguage", $"[{libraries}] {movie.Name}（ID {movie.Id}）：{message}");
    }

    private async Task<HashSet<string>> ScopeItems(HelperSettings config, List<string> libraryIds, CancellationToken token)
    {
        var folders = await Read(config, "Library/VirtualFolders", token);
        if (folders.ValueKind != JsonValueKind.Array || libraryIds.Any(id => !folders.EnumerateArray().Any(l => Text(l, "ItemId") == id)))
            throw new InvalidOperationException("所选媒体库已删除或无法访问，请重新检查媒体库。");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in libraryIds)
        {
            var offset = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                EnsureContext(config, true);
                var page = await Read(config, $"Items?ParentId={Uri.EscapeDataString(id)}&Recursive=true&IncludeItemTypes=Movie&SortBy=SortName&SortOrder=Ascending&StartIndex={offset}&Limit=200", token);
                if (!page.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("无法核对所选媒体库的影片范围，请重新检查媒体库。");
                var added = 0;
                foreach (var item in items.EnumerateArray())
                {
                    var itemId = Text(item, "Id");
                    if (itemId.Length == 0) throw new InvalidOperationException("媒体库返回了没有 ID 的影片，已停止补全。");
                    if (visited.Add(itemId)) added++;
                    result.Add(itemId);
                }
                var total = Number(page, "TotalRecordCount");
                if (items.GetArrayLength() == 0)
                {
                    if (offset < total) throw new InvalidOperationException("所选媒体库列表不完整，已停止补全。");
                    break;
                }
                if (added == 0) throw new InvalidOperationException("所选媒体库分页没有前进，已停止补全。");
                offset += items.GetArrayLength();
                if (total > 0 && offset >= total) break;
            }
        }
        return result;
    }

    private async Task<LibraryMovie?> ReadMovie(HelperSettings config, string id, CancellationToken token, TimeSpan timeout)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestTimeout.CancelAfter(timeout);
        var result = await Read(config, "Items?Ids=" + Uri.EscapeDataString(id) + "&Fields=MediaSources,MediaStreams,Path", requestTimeout.Token);
        if (!result.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Emby 未返回影片媒体信息。");
        return items.EnumerateArray().Where(i => Text(i, "Id") == id).Select(Analyze).FirstOrDefault();
    }

    private static bool SameSources(LibraryMovie before, LibraryMovie after) =>
        before.Sources.Count == after.Sources.Count && before.Sources.All(s => after.Sources.Any(a => a.Id == s.Id && a.Path == s.Path));

    private static bool ContainsTracks(LibraryMovie before, LibraryMovie after) => SameSources(before, after) &&
        before.Sources.All(s => s.Tracks.All(t => after.Sources.Single(a => a.Id == s.Id && a.Path == s.Path).Tracks.Any(a => a.Index == t.Index && a.Type == t.Type)));

    private static bool Verified(LibraryMovie before, LibraryMovie after) => after.Classification == "complete" && ContainsTracks(before, after);

    internal static LibraryMovie? Analyze(JsonElement item) => LibraryMediaAnalyzer.Analyze(item);


    private static string DescribeTracks(LibraryMovie movie)
    {
        return string.Join("；", movie.Sources.Select(source =>
        {
            static string ListTracks(IEnumerable<LibraryTrack> tracks) =>
                string.Join(", ", tracks.OrderBy(track => track.Index)
                    .Select(track => $"#{track.Index}={ (Unknown(track.Language) ? "und/未识别" : track.Language) }"));

            var audio = ListTracks(source.Tracks.Where(track => track.Type == "Audio"));
            var subtitles = ListTracks(source.Tracks.Where(track => track.Type == "Subtitle"));
            return $"媒体源 {source.Id}: 音轨 [{(audio.Length == 0 ? "未返回" : audio)}]，字幕 [{(subtitles.Length == 0 ? "未返回" : subtitles)}]，时长 {Runtime(source.RunTimeTicks)}";
        }));
    }
    private static string ProbeLanguages(JsonElement probe, string field)
    {
        if (!probe.TryGetProperty(field, out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
            return "none";
        return string.Join(", ", streams.EnumerateArray().Select(stream =>
        {
            var language = Text(stream, "language");
            return $"#{Number(stream, "index")}={(Unknown(language) ? "und" : language)}";
        }));
    }

    private static bool Unknown(string language) => string.IsNullOrWhiteSpace(language) || language.Trim().ToLowerInvariant() is "und" or "unk" or "unknown" or "undefined";
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static int Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) ? number : 0;
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static string Runtime(long? ticks) => ticks > 0 ? TimeSpan.FromTicks(ticks.Value).ToString(@"hh\:mm\:ss") : "未识别";
    private static string Runtime(JsonElement value, string name) => Runtime(value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var ticks) ? ticks : null);

    private async Task<JsonElement> Read(HelperSettings config, string route, CancellationToken token)
    {
        using var response = await Send(config, HttpMethod.Get, route, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
    }

    private async Task<HttpResponseMessage> Send(HelperSettings config, HttpMethod method, string route, CancellationToken token, object? body = null)
    {
        token.ThrowIfCancellationRequested(); EnsureContext(config);
        using var request = new HttpRequestMessage(method, config.EmbyUrl.TrimEnd('/') + "/emby/" + route);
        request.Headers.Add("X-Emby-Token", config.EmbyApiKey);
        if (body is not null) request.Content = EmbyJsonContent.Create(body);
        var response = await client.SendAsync(request, token);
        if (response.IsSuccessStatusCode) return response;
        using (response) throw await EmbyRequestFailure.From(response, method, route, config.EmbyApiKey, token);
    }

    private static string Error(Exception e) => e switch
    {
        EmbyRequestFailure failure => failure.Message,
        HttpRequestException h => "Emby 请求失败" + (h.StatusCode is { } code ? $"（HTTP {(int)code}）" : "") + "，请检查连接、管理权限及 Emby 日志。",
        OperationCanceledException => "Emby 请求超时，请检查服务器负载及网络。",
        JsonException => "Emby 返回的媒体信息或任务记录格式无法读取。",
        _ => HelperLog.Redact(e.Message)
    };

    private void Update(Action action) { lock (gate) { action(); Persist(); } }
    private void Persist()
    {
        try
        {
            var temporary = FilePath + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(plan, SettingsStore.Json));
            SettingsStore.Restrict(temporary); File.Move(temporary, FilePath, true);
            plan.PersistenceWarning = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (plan.PersistenceWarning is null) log.Add("Error", "LibraryLanguage", "无法保存任务记录，重启后可能丢失进度。请检查数据目录权限和可用空间。");
            plan.PersistenceWarning = "无法保存任务记录，重启后可能丢失进度，请检查数据目录。";
        }
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        if (worker is not null) await worker;
        cancellation?.Dispose(); shutdown.Dispose();
    }
}

