using System.Globalization;
using EmbyBlurayHelper.EmbyPlugin.Chapters;
using EmbyBlurayHelper.EmbyPlugin.Media;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

internal sealed class DiscThumbnailService(ILibraryManager library, IItemRepository repository,
    IServerApplicationHost host, IFileSystem files, ILogger log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static bool IsDisc(Video item) => BluRaySourceResolver.IsBluRaySource(item.Container, item.Path);
    private sealed class Frame(long position, long sample, string path)
    {
        internal long Position { get; } = position;
        internal long Sample { get; } = sample;
        internal string Path { get; } = path;
    }

    internal async Task<List<ChapterInfo>> Extract(Video item, bool duringScan, CancellationToken token,
        Action<double>? progress = null, List<ChapterInfo>? stagedChapters = null)
    {
        var options = library.GetLibraryOptions(item);
        var old = stagedChapters ?? repository.GetChapters(item);
        if (!IsDisc(item) || !LibraryProcessingScope.Allowed(library, item) || !DiscThumbnailPolicy.Enabled(options, duringScan)) return old;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        string? work = null;
        try
        {
            if (!LibraryProcessingScope.Allowed(library, item)) return old;
            options = library.GetLibraryOptions(item);
            if (!DiscThumbnailPolicy.Enabled(options, duringScan)) return old;
            var source = item.GetMediaSources(false, false, options).FirstOrDefault();
            if (source is null || !BluRaySourceResolver.IsBluRaySource(source.Container, source.Path)) return old;
            var target = BluRaySourceResolver.Resolve(library, item.InternalId.ToString(CultureInfo.InvariantCulture), source.Id);
            if (target.OwnerId != item.InternalId) return old;
            var runtime = source.RunTimeTicks.GetValueOrDefault();
            // Disc sources can expose a 1080p Dolby Vision enhancement track.
            // Choose the main resolution and explicitly map it, rather than
            // allowing ffmpeg to auto-select a different video stream.
            var stream = source.MediaStreams?.Where(s => s.Type == MediaStreamType.Video && !s.IsExternal)
                .OrderByDescending(s => (long)(s.Width ?? 0) * (s.Height ?? 0)).ThenBy(s => s.Index).FirstOrDefault();
            if (runtime <= 0 || stream is null) throw new InvalidOperationException("缺少有效时长或视频流，请先补全原盘媒体信息。");
            var signature = NativeChapterMerge.Signature(repository.GetChapters(item));
            var interval = DiscThumbnailPolicy.Interval(options);
            var directory = new DirectoryService(log, files);
            var existingBif = interval == -1 ? null : Video.GetThumbnailSetInfos(item.Path, item.Id, true, directory)
                .FirstOrDefault(b => b.IntervalSeconds == interval && b.Width == 320 && File.Exists(b.Path));
            var chapters = old.Select(Clone).ToList();
            var normal = NativeChapterMerge.Normal(chapters);
            var root = item.GetInternalMetadataPath();
            var stamp = item.DateModified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var frames = interval == -1
                ? normal.Where(c => c.StartPositionTicks >= 0 && c.StartPositionTicks < runtime && (!ValidImage(c.ImagePath) || c.ImagePath.EndsWith(".bif", StringComparison.OrdinalIgnoreCase)))
                    .Select(c => new Frame(c.StartPositionTicks, c.StartPositionTicks == 0 ? Math.Min(8 * TimeSpan.TicksPerSecond, runtime / 2) : c.StartPositionTicks,
                        Path.Combine(root, "chapters", stamp + "_" + c.StartPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg"))).ToArray()
                : existingBif is not null ? Array.Empty<Frame>() : Enumerable.Range(0, checked((int)Math.Ceiling(runtime / (double)(interval * TimeSpan.TicksPerSecond))))
                    .Select(i => new Frame(i * (long)interval * TimeSpan.TicksPerSecond, i == 0 ? Math.Min(TimeSpan.TicksPerSecond, runtime / 2) : i * (long)interval * TimeSpan.TicksPerSecond, "")).ToArray();
            if (frames.Length > 50000) throw new InvalidOperationException("截图数量超过安全上限，请在 Emby 中增大截图间隔。");
            if (interval == -1 && frames.Length == 0) { progress?.Invoke(100); return old; }
            work = Path.Combine(Path.GetTempPath(), "emby-bluray-thumbnails-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var rendered = new Dictionary<long, string>();
            var native = new EmbyImageExtractor(host);
            if (frames.Length > 0)
            {
                using var media = await DiscMediaSession.Open(Plugin.Instance.Configuration.ChapterReaderPath, source.Path, runtime, source.Size ?? 0, token).ConfigureAwait(false);
                log.Info("DiscThumbnails: {0} ({1}), {2}, playlist {3}, {4} images, {5}; video #{6}, {7}x{8}, {9}, transfer {10}. Emby controls extraction.", item.Name, item.InternalId,
                    duringScan ? "scan" : "scheduled task", media.Data.Playlist, frames.Length, interval == -1 ? "chapter markers" : interval + "s interval",
                    stream.Index, stream.Width, stream.Height, stream.ExtendedVideoSubType, stream.ColorTransfer ?? "unknown");
                var duration = Math.Min(runtime, media.Data.DurationTicks);
                foreach (var clip in media.Data.Clips)
                {
                    token.ThrowIfCancellationRequested();
                    var group = frames.Where(f => Math.Min(f.Sample, duration - 1) >= clip.TimelineStartTicks && Math.Min(f.Sample, duration - 1) < clip.TimelineEndTicks).ToArray();
                    if (group.Length == 0) continue;
                    var folder = Path.Combine(work, "clip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                    var times = group.Select(f => TimeSpan.FromTicks(Math.Max(0, Math.Min(f.Sample, duration - 1) - clip.TimelineStartTicks + clip.OffsetTicks))).ToArray();
                    var protocol = clip.InputPath.StartsWith("http://", StringComparison.Ordinal) ? MediaProtocol.Http : MediaProtocol.File;
                    for (var i = 0; i < group.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!LibraryProcessingScope.Allowed(library, item) || !DiscThumbnailPolicy.Enabled(library.GetLibraryOptions(item), duringScan))
                            throw new InvalidOperationException("Emby 已关闭此媒体库的截图提取，停止处理。");
                        var output = Path.Combine(folder, i.ToString("D6", CultureInfo.InvariantCulture) + ".jpg");
                        rendered.Add(group[i].Position, await native.One(source, clip.InputPath, stream, protocol, item.Video3DFormat, times[i], output, token).ConfigureAwait(false));
                        if (rendered.Count == 1) log.Info("DiscThumbnails: {0} ({1}), color processing: {2}.", item.Name, item.InternalId, native.ColorProcessing);
                        progress?.Invoke(95.0 * rendered.Count / frames.Length);
                    }
                }
                if (rendered.Count != frames.Length) throw new IOException("部分章节未匹配正片片段，未保存不完整截图。");
            }
            token.ThrowIfCancellationRequested();
            // Settings and source are checked again after expensive decoding.
            var currentOptions = library.GetLibraryOptions(item);
            if (!LibraryProcessingScope.Allowed(library, item) || !DiscThumbnailPolicy.Enabled(currentOptions, duringScan) || DiscThumbnailPolicy.Interval(currentOptions) != interval ||
                currentOptions.SaveLocalThumbnailSets != options.SaveLocalThumbnailSets ||
                NativeChapterMerge.Signature(repository.GetChapters(item)) != signature ||
                BluRaySourceResolver.FindCurrent(library, item, source) is not { } current || current.RunTimeTicks != source.RunTimeTicks || current.Size != source.Size)
                throw new InvalidOperationException("提取期间 Emby 设置、章节或媒体源发生变化，已停止提交，请重试。");
            string? bif = existingBif?.Path;
            if (interval == -1)
            {
                foreach (var frame in frames)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(frame.Path)!);
                    File.Copy(rendered[frame.Position], frame.Path, true);
                    foreach (var chapter in normal.Where(c => c.StartPositionTicks == frame.Position))
                    { chapter.ImagePath = frame.Path; chapter.ImageDateModified = files.GetLastWriteTimeUtc(frame.Path); }
                }
            }
            else
            {
                if (bif is null)
                {
                    var destination = options.SaveLocalThumbnailSets ? item.ContainingFolderPath : root;
                    Directory.CreateDirectory(destination);
                    bif = Path.Combine(destination, item.FileNameWithoutExtension + "-320-" + interval.ToString(CultureInfo.InvariantCulture) + ".bif");
                    var temporary = Path.Combine(work, "result.bif");
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                        await Video.CreateBif(output, interval * 1000, frames.Select(f => files.GetFileInfo(rendered[f.Position])).ToList()).ConfigureAwait(false);
                    File.Copy(temporary, bif, true);
                }
                foreach (var chapter in normal.Where(c => !ValidImage(c.ImagePath) || c.ImagePath.EndsWith(".bif", StringComparison.OrdinalIgnoreCase)))
                { chapter.ImagePath = bif; chapter.ImageDateModified = files.GetLastWriteTimeUtc(bif); }
            }
            repository.SaveChapters(item.InternalId, true, chapters);
            // Image association is part of chapter ownership, so update receipts
            // only when the previously managed list still exactly matches.
            var changedReceipt = false;
            foreach (var receipt in Plugin.Instance.Configuration.ChapterReceipts.Where(r => r.OwnerId == item.InternalId && r.Path == source.Path && r.Signature == signature))
            { receipt.Signature = NativeChapterMerge.Signature(chapters); changedReceipt = true; }
            if (changedReceipt) Plugin.Instance.SaveConfiguration();
            log.Info("DiscThumbnails: saved {0} images for {1} ({2}); {3}. Media streams and chapter positions retained.", frames.Length, item.Name, item.InternalId,
                interval == -1 ? "chapter JPEGs" : "Emby BIF " + interval + "s");
            progress?.Invoke(100);
            return chapters;
        }
        finally
        {
            try
            {
                if (work is not null)
                    try { Directory.Delete(work, true); }
                    catch (IOException e) { log.Warn("DiscThumbnails: temporary image cleanup failed: {0}", e.Message); }
                    catch (UnauthorizedAccessException e) { log.Warn("DiscThumbnails: temporary image cleanup failed: {0}", e.Message); }
            }
            finally { Gate.Release(); }
        }
    }
    private static bool ValidImage(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0;
    private static ChapterInfo Clone(ChapterInfo c) => new() { Name = c.Name, StartPositionTicks = c.StartPositionTicks,
        MarkerType = c.MarkerType, ImagePath = c.ImagePath, ImageDateModified = c.ImageDateModified };
}
