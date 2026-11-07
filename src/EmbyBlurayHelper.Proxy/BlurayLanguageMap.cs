using System.Text.Json;
using BDInfo;

namespace EmbyBlurayHelper.Proxy;

internal sealed record BlurayPlaylist(string Root, int Number);
internal sealed record BlurayMedia(IReadOnlyDictionary<ushort, string>? Languages, BlurayPlaylist? Playlist, string SourcePath = "");

internal static class BlurayLanguageMap
{
    public static IReadOnlyDictionary<ushort, string>? Load(JsonElement source, ILogger logger,
        IDictionary<ushort, string>? trackTypes = null)
        => Read(source, logger, trackTypes).Languages;

    public static BlurayMedia Read(JsonElement source, ILogger logger,
        IDictionary<ushort, string>? trackTypes = null)
    {
        if (!source.TryGetProperty("Container", out var container) ||
            !string.Equals(container.GetString(), "bluray", StringComparison.OrdinalIgnoreCase) ||
            !source.TryGetProperty("MediaStreams", out var mediaStreams) ||
            !mediaStreams.EnumerateArray().Any(s =>
                s.TryGetProperty("Type", out var type) && type.GetString() == "Video" &&
                s.TryGetProperty("Width", out var width) && width.GetInt32() >= 3840))
            return new(null, null);

        var path = source.GetProperty("Path").GetString();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return new(null, null);
        var bdmv = Directory.Exists(Path.Combine(path, "BDMV")) ? Path.Combine(path, "BDMV") : path;
        var disc = new BDROM(new BDInfo.IO.DirectoryInfo(Directory.GetParent(bdmv)!.FullName));
        Index(disc, bdmv);

        var runtime = source.GetProperty("RunTimeTicks").GetInt64() / 10_000_000d;
        var size = source.TryGetProperty("Size", out var sizeValue) ? sizeValue.GetInt64() : 0;
        var candidates = new List<TSPlaylistFile>();
        foreach (var playlist in disc.PlaylistFiles.Values)
        {
            try
            {
                playlist.Scan(disc.StreamFiles, disc.StreamClipFiles);
                if (playlist.StreamClips.Count > 0 && Math.Abs(playlist.TotalLength - runtime) <= Math.Max(10, runtime * 0.002))
                    candidates.Add(playlist);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Malformed alternate playlists must not prevent normal playback.
            }
        }

        if (candidates.Count == 0) return new(null, null);
        // Emby's Size is the sum of its selected clip files. Exact matches resolve
        // localized branches that have almost identical durations.
        var exactSize = candidates.Where(p => size > 0 && p.FileSize == (ulong)size).ToArray();
        var selected = exactSize.Length > 0 ? exactSize : candidates.ToArray();
        BlurayPlaylist? playback = null;
        // Never guess a branch from duration alone. Only a uniquely matching
        // multi-clip title is eligible for the static-stream workaround.
        if (exactSize.Length == 1 && selected[0].StreamClips.Count > 1 &&
            int.TryParse(Path.GetFileNameWithoutExtension(disc.PlaylistFiles.First(p => ReferenceEquals(p.Value, selected[0])).Key), out var number))
            playback = new(Directory.GetParent(bdmv)!.FullName, number);
        var maps = new List<Dictionary<ushort, string>>();
        var types = new List<(ushort Pid, string Type)>();
        foreach (var playlist in selected)
        {
            var clipMaps = new List<Dictionary<ushort, string>>();
            foreach (var name in playlist.StreamClips.Select(c => Path.ChangeExtension(c.Name, ".CLPI")).Distinct())
            {
                if (!disc.StreamClipFiles.TryGetValue(name.ToUpperInvariant(), out var clip)) return new(null, null);
                clip.Scan();
                if (trackTypes is not null)
                    types.AddRange(clip.Streams.Values.Where(s => s.IsAudioStream || s.IsTextStream || s.IsGraphicsStream)
                        .Select(s => (s.PID, s.IsAudioStream ? "Audio" : "Subtitle")));
                clipMaps.Add(clip.Streams.Values.Where(s => (s.IsAudioStream || s.IsTextStream || s.IsGraphicsStream) && ValidLanguage(s.LanguageCode))
                    .ToDictionary(s => s.PID, s => s.LanguageCode.ToLowerInvariant()));
            }
            playlist.Scan(disc.StreamFiles, disc.StreamClipFiles);
            if (trackTypes is not null)
                types.AddRange(playlist.PlaylistStreams.Values.Where(s => s.IsAudioStream || s.IsTextStream || s.IsGraphicsStream)
                    .Select(s => (s.PID, s.IsAudioStream ? "Audio" : "Subtitle")));
            var map = Unambiguous(clipMaps);
            // Playlist stream languages take priority over clip-level defaults.
            foreach (var stream in playlist.PlaylistStreams.Values.Where(s => (s.IsAudioStream || s.IsTextStream || s.IsGraphicsStream) && ValidLanguage(s.LanguageCode)))
                map[stream.PID] = stream.LanguageCode.ToLowerInvariant();
            maps.Add(map);
        }

        var languages = Unambiguous(maps);
        if (trackTypes is not null)
            foreach (var group in types.GroupBy(t => t.Pid).Where(g => languages.ContainsKey(g.Key) && g.Select(t => t.Type).Distinct().Count() == 1))
                trackTypes[group.Key] = group.First().Type;
        logger.LogInformation("Infuse UHD language patch: {Count} PID language entries from {CountPlaylists} matching playlists.",
            languages.Count, selected.Length);
        return new(languages.Count > 0 ? languages : null, playback, path);
    }

    private static Dictionary<ushort, string> Unambiguous(IEnumerable<Dictionary<ushort, string>> maps) => maps
        .SelectMany(m => m).GroupBy(p => p.Key)
        .Where(g => g.Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        .ToDictionary(g => g.Key, g => g.First().Value);

    private static bool ValidLanguage(string? language) => language is { Length: 3 } &&
        language.All(char.IsAsciiLetter) && !language.Equals("und", StringComparison.OrdinalIgnoreCase);

    private static void Index(BDROM disc, string bdmv)
    {
        if (disc.PlaylistFiles.Count == 0)
            foreach (var path in Files(bdmv, "PLAYLIST", ".mpls"))
                disc.PlaylistFiles.Add(Path.GetFileName(path).ToUpperInvariant(),
                    new TSPlaylistFile(disc, BDInfo.IO.FileInfo.FromFullName(path)));
        if (disc.StreamFiles.Count == 0)
            foreach (var path in Files(bdmv, "STREAM", ".m2ts"))
                disc.StreamFiles.Add(Path.GetFileName(path).ToUpperInvariant(), new TSStreamFile(BDInfo.IO.FileInfo.FromFullName(path)));
        if (disc.StreamClipFiles.Count == 0)
            foreach (var path in Files(bdmv, "CLIPINF", ".clpi"))
                disc.StreamClipFiles.Add(Path.GetFileName(path).ToUpperInvariant(), new TSStreamClipFile(BDInfo.IO.FileInfo.FromFullName(path)));
    }

    private static IEnumerable<string> Files(string bdmv, string directory, string extension) =>
        Directory.EnumerateFiles(Path.Combine(bdmv, directory)).Where(p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase));
}
