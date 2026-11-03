using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin.Chapters;

// Conservative ownership: only empty, machine-generated, or unchanged managed lists.
internal static class NativeChapterMerge
{
    internal static ChapterInfo[] Normal(IEnumerable<ChapterInfo> chapters) => chapters.Where(c => c.MarkerType == MarkerType.Chapter).ToArray();
    internal static string Signature(IEnumerable<ChapterInfo> chapters)
    {
        using var hash = SHA256.Create();
        var text = string.Join("\n", chapters.Select(c => c.MarkerType + "|" + c.StartPositionTicks.ToString(CultureInfo.InvariantCulture) + "|" + c.Name + "|" + c.ImagePath));
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
    internal static bool Generated(IEnumerable<ChapterInfo> chapters, int intervalMinutes)
    {
        var list = Normal(chapters);
        if (intervalMinutes <= 0 || list.Length < 2) return false;
        return list.Select((c, i) => c.StartPositionTicks == i * (long)intervalMinutes * TimeSpan.TicksPerMinute &&
            (string.IsNullOrEmpty(c.Name) || Regex.IsMatch(c.Name, @"^(?:Chapter|章节|章節)\s*0*" + (i + 1) + "$", RegexOptions.IgnoreCase))).All(x => x);
    }
    internal static bool Matches(IEnumerable<ChapterInfo> current, long[] native)
    {
        var list = Normal(current);
        return list.Length == native.Length && list.Select((c, i) => Math.Abs(c.StartPositionTicks - native[i]) <= TimeSpan.TicksPerMillisecond * 50).All(x => x);
    }
    internal static bool CanManage(IEnumerable<ChapterInfo> current, bool managed, int interval) =>
        managed || Normal(current).Length == 0 || Generated(current, interval);
    internal static List<ChapterInfo> Merge(IReadOnlyList<ChapterInfo> current, long[] native, long duration, bool managed, int interval)
    {
        if (native.Length == 0 || native.Length > 10000 || duration <= 0 || native.Any(t => t < 0 || t >= duration) ||
            native.Zip(native.Skip(1), (a, b) => a < b).Any(ok => !ok)) throw new InvalidDataException("原生章节时间无效。");
        if (Matches(current, native)) return current.ToList();
        if (!CanManage(current, managed, interval))
            throw new InvalidOperationException("现有章节包含自定义或非等间隔内容，已保留，未覆盖。");
        return current.Where(c => c.MarkerType != MarkerType.Chapter).Concat(native.Select((t, i) =>
            new ChapterInfo { StartPositionTicks = t, Name = "Chapter " + (i + 1), MarkerType = MarkerType.Chapter }))
            .OrderBy(c => c.StartPositionTicks).ToList();
    }
}
