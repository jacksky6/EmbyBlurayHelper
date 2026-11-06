using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BDInfo;

namespace EmbyBlurayHelper.Proxy.Chapters;

internal sealed record DiscChapterPresentation(VirtualUdfImage Image, string Signature, string Playlist, int ChapterCount)
{
    // Raised when the playlist choice rule changes, so saved listing records
    // are decided once more under the new rule.
    internal const int SelectionRevision = 2;
    internal sealed record Title(string Path, double Length, ulong Size, string[] Clips, double[] Marks, int Streams);
    private (string Path, long Length, long Modified)[] sourceFiles = [];
    internal bool IsCurrent => sourceFiles.All(s => { var f = new FileInfo(s.Path); return f.Exists && f.Length == s.Length && f.LastWriteTimeUtc.Ticks == s.Modified; });
    // Only an authenticated Emby media source is accepted. ISO already carries
    // its disc navigation; this first presentation path handles BDMV folders.
    internal static DiscChapterPresentation? Create(JsonElement source)
    {
        if (!source.TryGetProperty("Container", out var format) || !string.Equals(format.GetString(), "bluray", StringComparison.OrdinalIgnoreCase) ||
            !source.TryGetProperty("Path", out var pathValue) || !source.TryGetProperty("Chapters", out var chapterValues)) return null;
        var path = pathValue.GetString();
        if (string.IsNullOrWhiteSpace(path)) return null;
        var runtime = source.GetProperty("RunTimeTicks").GetInt64();
        var chapters = chapterValues.EnumerateArray().Where(c => !c.TryGetProperty("MarkerType", out var marker) || marker.GetString() is null or "Chapter")
            .Select(c => c.GetProperty("StartPositionTicks").GetInt64()).ToArray();
        if (chapters.Length == 0) return null;
        if (chapters.Length > 10000 || runtime <= 0 || chapters.Any(c => c < 0 || c >= runtime) || !chapters.SequenceEqual(chapters.Distinct().Order()))
            throw new InvalidDataException("Emby 章节时间点无效，保留原播放方式。");
        // Decide from Emby data first; touch the disk only for a real candidate.
        // A missing folder is usually an unmounted volume, so it is reported as
        // a temporary I/O failure rather than a property of the disc.
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("原盘目录当前不可访问。");
        var bdmv = Child(path, "BDMV", true) ?? path;
        var playlistDirectory = Child(bdmv, "PLAYLIST", true) ?? throw new InvalidDataException("BDMV 缺少 PLAYLIST。");
        var clipDirectory = Child(bdmv, "CLIPINF", true) ?? throw new InvalidDataException("BDMV 缺少 CLIPINF。");
        var streamDirectory = Child(bdmv, "STREAM", true) ?? throw new InvalidDataException("BDMV 缺少 STREAM。");
        var disc = new BDROM(new BDInfo.IO.DirectoryInfo(Directory.GetParent(bdmv)!.FullName));
        if (disc.StreamFiles.Count == 0) foreach (var file in Directory.EnumerateFiles(streamDirectory).Where(p => Path.GetExtension(p).Equals(".m2ts", StringComparison.OrdinalIgnoreCase)))
                disc.StreamFiles.Add(Path.GetFileName(file).ToUpperInvariant(), new TSStreamFile(BDInfo.IO.FileInfo.FromFullName(file)));
        if (disc.StreamClipFiles.Count == 0) foreach (var file in Directory.EnumerateFiles(clipDirectory).Where(p => Path.GetExtension(p).Equals(".clpi", StringComparison.OrdinalIgnoreCase)))
                disc.StreamClipFiles.Add(Path.GetFileName(file).ToUpperInvariant(), new TSStreamClipFile(BDInfo.IO.FileInfo.FromFullName(file)));
        var titles = new List<Title>();
        foreach (var file in Directory.EnumerateFiles(playlistDirectory).Where(p => Path.GetExtension(p).Equals(".mpls", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var p = new TSPlaylistFile(disc, BDInfo.IO.FileInfo.FromFullName(file)); p.Scan(disc.StreamFiles, disc.StreamClipFiles);
                if (p.StreamClips.Count > 0) titles.Add(new(file, p.TotalLength, p.FileSize, p.StreamClips.Select(c => c.Name).ToArray(),
                    p.Chapters.Where(t => double.IsFinite(t) && t >= 0 && t < p.TotalLength).Distinct().Order().ToArray(), p.PlaylistStreams.Count));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        var selected = Choose(titles, runtime, source.TryGetProperty("Size", out var sizeValue) ? sizeValue.GetInt64() : 0);
        var clips = selected.Clips;
        var files = new List<VirtualUdfImage.ImageFile>();
        var originals = new List<string> { selected.Path };
        foreach (var name in new[] { "index.bdmv", "MovieObject.bdmv" })
        {
            var file = Child(bdmv, name, false); if (file is null) continue;
            originals.Add(file);
            var b = SmallFile(file); files.Add(new("BDMV/" + name, null, b.Length, b));
        }
        var original = SmallFile(selected.Path);
        var patched = MplsChapterPatch.Apply(original, chapters);
        files.Add(new("BDMV/PLAYLIST/" + Path.GetFileNameWithoutExtension(selected.Path) + ".mpls", null, patched.Length, patched));
        var stamps = new List<string>();
        foreach (var id in clips.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var clpi = Child(clipDirectory, id + ".clpi", false) ?? throw new InvalidDataException("原盘缺少 CLPI。");
            var clip = Child(streamDirectory, id + ".m2ts", false) ?? throw new InvalidDataException("原盘缺少 M2TS。");
            var b = SmallFile(clpi); files.Add(new("BDMV/CLIPINF/" + id + ".clpi", null, b.Length, b));
            var info = new FileInfo(clip); files.Add(new("BDMV/STREAM/" + id + ".m2ts", clip, info.Length));
            originals.Add(clpi); originals.Add(clip);
            stamps.Add(info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
            if (files.Count > 1024 || files.Sum(f => f.Bytes?.Length ?? 0) > 16 * 1024 * 1024) throw new InvalidDataException("虚拟原盘导航超出内存上限。");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // The same source files can produce different byte offsets after a
        // filesystem fix. Invalidate old URLs and HTTP validators as well.
        hash.AppendData(Encoding.UTF8.GetBytes("EmbyBlurayHelper.UDF/" + VirtualUdfImage.FormatRevision + "\n"));
        foreach (var file in files.Where(f => f.Bytes is not null)) { hash.AppendData(Encoding.UTF8.GetBytes(file.Name)); hash.AppendData(file.Bytes!); }
        hash.AppendData(Encoding.UTF8.GetBytes(string.Join("\n", stamps)));
        var signature = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new(VirtualUdfImage.Create(files), signature, Path.GetFileName(selected.Path), chapters.Length)
        { sourceFiles = originals.Select(p => { var f = new FileInfo(p); return (f.FullName, f.Length, f.LastWriteTimeUtc.Ticks); }).ToArray() };
    }
    // Discs often carry the feature twice: a commentary entry whose stream
    // table keeps only the stereo and commentary tracks, next to the full one,
    // with marks rounded a millisecond apart. Those are one branch; the player
    // gets the fullest stream table, where commentary is one track among all.
    internal static Title Choose(IReadOnlyList<Title> titles, long runtime, long size)
    {
        var seconds = runtime / 10000000d;
        var candidates = titles.Where(t => Math.Abs(t.Length - seconds) <= Math.Max(10, seconds * .002)).ToArray();
        var exact = candidates.Where(t => size > 0 && t.Size == (ulong)size).ToArray();
        var matches = exact.Length > 0 ? exact : candidates.Where(t => Math.Abs(t.Length - seconds) <= 2).ToArray();
        if (matches.Length == 0) throw new InvalidDataException("无法匹配 Emby 正片版本，保留原播放方式。");
        var first = matches[0];
        if (matches.Skip(1).Any(t => !t.Clips.SequenceEqual(first.Clips, StringComparer.OrdinalIgnoreCase) || t.Marks.Length != first.Marks.Length ||
            t.Marks.Zip(first.Marks).Any(m => Math.Abs(m.First - m.Second) > .05))) throw new InvalidDataException("正片存在不同分支，无法确认章节对应关系。");
        return matches.OrderByDescending(t => t.Streams).ThenBy(t => Path.GetFileName(t.Path), StringComparer.Ordinal).First();
    }
    private static string? Child(string root, string name, bool directory) => (directory ? Directory.EnumerateDirectories(root) : Directory.EnumerateFiles(root))
        .FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
    private static byte[] SmallFile(string path) { if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("原盘导航文件过大。"); return File.ReadAllBytes(path); }
}

internal static class MplsChapterPatch
{
    internal static byte[] Apply(byte[] input, IReadOnlyList<long> ticks)
    {
        static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
        static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
        if (input.Length < 40 || !input.AsSpan(0, 4).SequenceEqual("MPLS"u8) || ticks.Count is 0 or > 10000) throw new InvalidDataException("Invalid MPLS.");
        var start = checked((int)U32(input, 8)); var end = checked(start + 4 + (int)U32(input, start));
        if (start < 20 || end > input.Length || start + 10 > end) throw new InvalidDataException("Invalid MPLS playlist section.");
        var count = U16(input, start + 6); var cursor = start + 10;
        var items = new List<(uint In, uint Out)>();
        for (var i = 0; i < count; i++)
        {
            var p = cursor + 2; var next = checked(p + U16(input, cursor));
            if (next > end || next - p < 32) throw new InvalidDataException("Invalid MPLS play item.");
            var first = U32(input, p + 12); var last = U32(input, p + 16); if (last <= first) throw new InvalidDataException("Invalid MPLS timing.");
            items.Add((first, last)); cursor = next;
        }
        var result = new byte[checked(input.Length + 6 + 14 * ticks.Count)]; input.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), (uint)input.Length);
        var marks = result.AsSpan(input.Length); BinaryPrimitives.WriteUInt32BigEndian(marks, (uint)(marks.Length - 4));
        BinaryPrimitives.WriteUInt16BigEndian(marks[4..], (ushort)ticks.Count);
        long previous = -1;
        for (var i = 0; i < ticks.Count; i++)
        {
            if (ticks[i] < 0 || ticks[i] <= previous) throw new InvalidDataException("Chapter times must be strictly increasing."); previous = ticks[i];
            var time = checked((long)Math.Round(ticks[i] * 45000d / TimeSpan.TicksPerSecond)); var clip = 0;
            while (clip < items.Count && time >= items[clip].Out - items[clip].In) { time -= items[clip].Out - items[clip].In; clip++; }
            if (clip >= items.Count) throw new InvalidDataException("Emby chapter lies outside the selected playlist.");
            var mark = marks.Slice(6 + 14 * i, 14); mark[1] = 1; BinaryPrimitives.WriteUInt16BigEndian(mark[2..], (ushort)clip);
            BinaryPrimitives.WriteUInt32BigEndian(mark[4..], checked(items[clip].In + (uint)time)); BinaryPrimitives.WriteUInt16BigEndian(mark[8..], ushort.MaxValue);
        }
        return result;
    }
}
