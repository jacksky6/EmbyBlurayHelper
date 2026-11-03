using EmbyBlurayHelper.EmbyPlugin.Media;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin.Chapters;

internal sealed class ChapterWriter(ILibraryManager library, IItemRepository repository)
{
    private List<ChapterInfo> Saved(long id) => repository.GetChapters(library.GetItemById(id) ?? throw new InvalidOperationException("章节所属影片已不存在。"));
    internal object Inspect(BluRayProbeTarget target)
    {
        var saved = Saved(target.OwnerId);
        var signature = NativeChapterMerge.Signature(saved);
        var receipt = Plugin.Instance.Configuration.ChapterReceipts.FirstOrDefault(r => r.OwnerId == target.OwnerId && r.Path == target.Source.Path && r.Signature == signature);
        var managed = receipt is not null;
        var interval = library.GetLibraryOptions(target.Item).AutoGenerateChapterIntervalMinutes;
        var state = NativeChapterMerge.Normal(saved).Length == 0 ? "empty" : managed ? "managed" : NativeChapterMerge.Generated(saved, interval) ? "generated" : "existing";
        return new { state, count = NativeChapterMerge.Normal(saved).Length, signature, playlist = receipt?.Playlist ?? "", mediaSourceId = target.Source.Id };
    }

    internal async Task<object> Fill(BluRayProbeTarget target, string expectedSignature, CancellationToken token)
    {
        var config = Plugin.Instance.Configuration;
        if (!config.EnableNativeChapters) throw new InvalidOperationException("原盘章节补全未开启。");
        var old = Saved(target.OwnerId);
        var signature = NativeChapterMerge.Signature(old);
        if (expectedSignature.Length > 0 && expectedSignature != signature)
            throw new InvalidOperationException("检查之后章节已变化，请重新检查，未覆盖现有章节。");
        var data = await ChapterProcess.Read(config.ChapterReaderPath, target.Source.Path, target.Source.RunTimeTicks ?? 0, target.Source.Size ?? 0, token).ConfigureAwait(false);
        var owner = library.GetItemById(target.OwnerId);
        var source = owner is null ? null : BluRaySourceResolver.FindCurrent(library, owner, target.Source);
        if (source is null || source.RunTimeTicks != target.Source.RunTimeTicks || source.Size != target.Source.Size || NativeChapterMerge.Signature(Saved(target.OwnerId)) != signature)
            throw new InvalidOperationException("读取期间媒体源或章节已变化，未写入章节。");
        if (source.RunTimeTicks is > 0 && data.MatchReason is not ("exact-clip-size-and-runtime" or "runtime-and-equivalent-chapters"))
            throw new InvalidOperationException("所选原盘正片与 Emby 时长不一致，已跳过，避免写入错误版本的章节。");
        var managed = config.ChapterReceipts.Any(r => r.OwnerId == target.OwnerId && r.Path == source.Path && r.Signature == signature);
        var timelineEnd = source.RunTimeTicks is > 0 ? Math.Min(data.DurationTicks, source.RunTimeTicks.Value) : data.DurationTicks;
        var merged = NativeChapterMerge.Merge(old, data.Chapters, timelineEnd, managed, library.GetLibraryOptions(target.Item).AutoGenerateChapterIntervalMinutes);
        token.ThrowIfCancellationRequested();
        if (!Plugin.Instance.Configuration.EnableNativeChapters) throw new InvalidOperationException("原盘章节补全已关闭。");
        var changed = NativeChapterMerge.Signature(merged) != signature;
        if (changed) repository.SaveChapters(target.OwnerId, merged);
        var saved = Saved(target.OwnerId);
        if (!NativeChapterMerge.Matches(saved, data.Chapters) || NativeChapterMerge.Signature(saved) != NativeChapterMerge.Signature(merged))
            throw new InvalidOperationException("Emby 章节读回核验失败，请重新检查。");
        // Matching pre-existing native/custom lists remain user-owned. A no-op
        // must not grant future permission to replace a user's names or marks.
        var receipt = new ChapterReceipt { OwnerId = target.OwnerId, Path = source.Path, Signature = NativeChapterMerge.Signature(saved),
            SourceStamp = data.SourceStamp, Playlist = data.Playlist };
        var own = NativeChapterMerge.CanManage(old, managed, library.GetLibraryOptions(target.Item).AutoGenerateChapterIntervalMinutes);
        config.ChapterReceipts = config.ChapterReceipts.Where(r => r.OwnerId != target.OwnerId || r.Path != source.Path).Concat(own ? new[] { receipt } : Array.Empty<ChapterReceipt>()).ToList();
        Plugin.Instance.SaveConfiguration();
        return new { verified = true, changed, name = target.Item.Name, mediaSourceId = source.Id, count = data.Chapters.Length,
            playlist = data.Playlist, matchReason = data.MatchReason, signature = NativeChapterMerge.Signature(saved), seconds = data.Chapters.Select(t => t / (double)TimeSpan.TicksPerSecond).ToArray() };
    }
}
