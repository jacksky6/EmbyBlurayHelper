using System.Security.Cryptography;
using System.Text;
using DiscUtils.Udf;

namespace EmbyBlurayHelper.ProbeWrapper;

// Navigation only: never reads/decodes M2TS payload or invokes ffprobe.
internal static class DiscChapterReader
{
    internal static object Read(string path, string? playlistName, CancellationToken token, long runtimeTicks = 0, long sourceSize = 0)
    {
        var before = Stamp(path);
        using var image = Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
        using var udf = image is null ? null : new UdfReader(image);
        using var metadata = udf is null ? IsoDiscMetadata.OpenDirectory(Index(path), token) : IsoDiscMetadata.Open(udf, token);
        var selection = metadata.SelectChapterPlaylist(token, runtimeTicks, sourceSize, image is not null);
        var playlist = playlistName is null ? selection.Playlist : metadata.SelectPlaylist(token, playlistName);
        var duration = checked((long)Math.Round(playlist.TotalLength * TimeSpan.TicksPerSecond));
        var chapters = playlist.Chapters.Where(t => double.IsFinite(t) && t >= 0 && t < playlist.TotalLength)
            .Select(t => checked((long)Math.Round(t * TimeSpan.TicksPerSecond))).Distinct().Order().ToArray();
        if (duration <= 0 || chapters.Length == 0 || chapters.Length > 10000)
            throw new InvalidDataException("所选正片没有可用的原生章节，保留现有章节。");
        if (before != Stamp(path)) throw new IOException("读取期间原盘导航发生变化，请重试。");
        return new { Schema = 1, Path = path, SourceStamp = before, Playlist = playlist.Name, MatchReason = selection.Match, DurationTicks = duration, Chapters = chapters };
    }

    private static string Index(string path) => File.Exists(path) && Path.GetFileName(path).Equals("index.bdmv", StringComparison.OrdinalIgnoreCase)
        ? path : File.Exists(Path.Combine(path, "index.bdmv")) ? Path.Combine(path, "index.bdmv") : Path.Combine(path, "BDMV", "index.bdmv");
    internal static string Stamp(string path)
    {
        var files = new[] { path };
        if (!Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(Index(path))!;
            files = new[] { directory, Path.Combine(directory, "PLAYLIST"), Path.Combine(directory, "CLIPINF") }
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly))
                .Where(p => new[] { ".mpls", ".clpi", ".bdmv" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .Order(StringComparer.Ordinal).ToArray();
        }
        var text = string.Join("\n", files.Select(p => { var f = new FileInfo(p); return p + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks; }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
