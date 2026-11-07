using BDInfo;
using DiscUtils.Udf;

namespace EmbyBlurayHelper.ProbeWrapper;

// Only the small navigation files are materialized. M2TS stays inside the ISO.
internal sealed class IsoDiscMetadata : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "emby-bluray-helper-iso-" + Guid.NewGuid().ToString("N"));
    internal BDROM Disc { get; private set; } = null!;
    internal Dictionary<string, string> StreamPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    private List<TSPlaylistFile> validPlaylists = new();

    internal static IsoDiscMetadata OpenDirectory(string path, CancellationToken token)
    {
        var metadata = new IsoDiscMetadata();
        try
        {
            var bdmv = Path.GetDirectoryName(path)!; // Explicit BDMV/index.bdmv probe only.
            foreach (var file in Directory.EnumerateFiles(Path.Combine(bdmv, "STREAM")))
                if (Path.GetExtension(file).Equals(".m2ts", StringComparison.OrdinalIgnoreCase))
                    metadata.StreamPaths.Add(Path.GetFileName(file), file);
            metadata.Disc = new BDROM(new BDInfo.IO.DirectoryInfo(Directory.GetParent(bdmv)!.FullName));
            BluRayLanguageAugmenter.EnsureDiscIndexes(metadata.Disc);
            foreach (var clip in metadata.Disc.StreamClipFiles.Values)
            {
                token.ThrowIfCancellationRequested();
                try { clip.Scan(); } catch (Exception e) when (e is not OperationCanceledException) { }
            }
            return metadata;
        }
        catch { metadata.Dispose(); throw; }
    }

    internal static IsoDiscMetadata Open(UdfReader udf, CancellationToken token)
    {
        var metadata = new IsoDiscMetadata();
        try
        {
            if (!udf.DirectoryExists("\\BDMV"))
                throw new InvalidDataException("ISO has no BDMV directory (DVD ISO is not supported by this reader).");
            var bdmv = Path.Combine(metadata.root, "BDMV");
            foreach (var directory in new[] { "PLAYLIST", "CLIPINF", "STREAM" })
                Directory.CreateDirectory(Path.Combine(bdmv, directory));
            BluRayLanguageAugmenter.CopyUdfFiles(udf, "\\BDMV\\PLAYLIST", "*.mpls", Path.Combine(bdmv, "PLAYLIST"));
            BluRayLanguageAugmenter.CopyUdfFiles(udf, "\\BDMV\\CLIPINF", "*.clpi", Path.Combine(bdmv, "CLIPINF"));
            if (udf.FileExists("\\BDMV\\index.bdmv"))
                BluRayLanguageAugmenter.CopyUdfFile(udf, "\\BDMV\\index.bdmv", Path.Combine(bdmv, "index.bdmv"));
            foreach (var path in udf.GetFiles("\\BDMV\\STREAM", "*.m2ts", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path.Replace('\\', '/'));
                metadata.StreamPaths.Add(name, path);
                using var placeholder = File.Create(Path.Combine(bdmv, "STREAM", name));
            }
            metadata.Disc = new BDROM(new BDInfo.IO.DirectoryInfo(metadata.root));
            BluRayLanguageAugmenter.EnsureDiscIndexes(metadata.Disc);
            foreach (var clip in metadata.Disc.StreamClipFiles.Values)
            {
                token.ThrowIfCancellationRequested();
                // A damaged extra must not prevent scanning an intact feature playlist.
                try { clip.Scan(); } catch (Exception e) when (e is not OperationCanceledException) { }
            }
            return metadata;
        }
        catch { metadata.Dispose(); throw; }
    }

    internal TSPlaylistFile SelectPlaylist(CancellationToken token, string? requestedName = null)
    {
        var candidates = new List<TSPlaylistFile>();
        var repetitivePlaylists = new List<(string Name, double Duration, int Repetitions)>();
        foreach (var playlist in Disc.PlaylistFiles.Values)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                playlist.Scan(Disc.StreamFiles, Disc.StreamClipFiles);
                var clips = playlist.StreamClips.Where(c => c.AngleIndex == 0).ToArray();
                // Some damaged/authoring-tool playlists repeat one M2TS entry hundreds of
                // times. BDInfo reports their summed duration as a false multi-hour title.
                if (IsRepetitive(clips.Select(c => (c.Name, c.TimeIn, c.TimeOut))))
                {
                    repetitivePlaylists.Add((playlist.Name, playlist.TotalLength, clips.Length));
                    continue;
                }
                if (!playlist.HasLoops && double.IsFinite(playlist.TotalLength) && playlist.TotalLength > 0 &&
                    clips.Length > 0 && clips.All(c => StreamPaths.ContainsKey(c.Name) && c.Length > 0) &&
                    playlist.PlaylistStreams.Values.Any(s => s.IsVideoStream) &&
                    playlist.PlaylistStreams.Values.Any(s => s.IsAudioStream)) candidates.Add(playlist);
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        if (repetitivePlaylists.Count > 0)
        {
            var examples = repetitivePlaylists.Take(8).Select(p =>
                $"{p.Name} ({p.Duration:F1}s, {p.Repetitions} repeats)").ToArray();
            var remaining = repetitivePlaylists.Count - examples.Length;
            Console.Error.WriteLine("EmbyBlurayHelper.ProbeWrapper: ignored " + repetitivePlaylists.Count +
                " playlist(s) dominated by repeated M2TS intervals: " + string.Join(", ", examples) +
                (remaining > 0 ? $", and {remaining} more" : string.Empty) + ".");
        }
        var selected = requestedName is not null
            ? candidates.FirstOrDefault(p => p.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            : null;
        validPlaylists = candidates;
        if (requestedName is not null && selected is null)
            throw new InvalidDataException($"Requested ISO playlist {requestedName} is missing or is not a complete, non-looping audio/video playlist.");
        selected ??= candidates.OrderByDescending(p => p.TotalLength).ThenBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException("ISO has no complete, non-looping Blu-ray audio/video playlist.");
        var alternatives = candidates.Count(p => p != selected && p.TotalLength >= selected.TotalLength * .99);
        Console.Error.WriteLine($"EmbyBlurayHelper.ProbeWrapper: Blu-ray selected {selected.Name}: {selected.TotalLength:F3}s, " +
            $"{selected.StreamClips.Count(c => c.AngleIndex == 0)} clips ({(requestedName is null ? "longest complete non-looping playlist" : "requested playlist")}; {alternatives} similar-length alternatives).");
        return selected;
    }

    internal (TSPlaylistFile Playlist, string Match) SelectChapterPlaylist(CancellationToken token, long runtimeTicks, long sourceSize, bool iso, bool requireEquivalentClips = false)
    {
        var longest = SelectPlaylist(token);
        if (runtimeTicks <= 0) return (longest, "longest-complete-playlist");
        var seconds = runtimeTicks / (double)TimeSpan.TicksPerSecond;
        // Emby may save ffprobe's transport duration, which can differ from MPLS
        // by a few seconds. Exact selected-clip bytes disambiguate that case.
        var exact = iso || sourceSize <= 0 ? Array.Empty<TSPlaylistFile>() : validPlaylists.Where(p =>
            p.FileSize == (ulong)sourceSize && Math.Abs(p.TotalLength - seconds) <= Math.Max(10, seconds * .002)).ToArray();
        var matches = exact.Length > 0 ? exact : validPlaylists.Where(p => Math.Abs(p.TotalLength - seconds) <= 2).ToArray();
        if (matches.Length == 0) throw new InvalidDataException("没有与 Emby 当前媒体源时长/片段大小一致的播放列表；保留原章节。");
        static double[] Marks(TSPlaylistFile p) => p.Chapters.Where(t => double.IsFinite(t) && t >= 0 && t < p.TotalLength).Distinct().Order().ToArray();
        var first = Marks(matches[0]);
        if (matches.Skip(1).Any(p => { var other = Marks(p); return first.Length != other.Length || first.Zip(other).Any(t => Math.Abs(t.First - t.Second) > .05); }))
            throw new InvalidDataException("存在多个匹配版本且章节时间不同，无法可靠确定正片；保留原章节。");
        if (requireEquivalentClips)
        {
            // Chapters alone are enough for chapter extraction, but not for
            // streaming: equal-length branches can contain different pictures.
            var clips = matches[0].StreamClips.Select(c => (c.Name.ToUpperInvariant(), c.TimeIn, c.TimeOut, c.AngleIndex)).ToArray();
            if (matches.Skip(1).Any(p => !clips.SequenceEqual(p.StreamClips.Select(c => (c.Name.ToUpperInvariant(), c.TimeIn, c.TimeOut, c.AngleIndex)))))
                throw new InvalidDataException("Multiple matching Blu-ray playlists contain different clip sequences; original playback retained.");
        }
        var selected = matches.OrderBy(p => Math.Abs(p.TotalLength - seconds)).ThenBy(p => p.Name, StringComparer.Ordinal).First();
        var reason = exact.Length > 0 ? "exact-clip-size-and-runtime" : "runtime-and-equivalent-chapters";
        Console.Error.WriteLine($"NativeChapters: selected {selected.Name}, {reason}, {selected.TotalLength:F3}s; Emby {seconds:F3}s.");
        return (selected, reason);
    }

    // The same M2TS can legitimately occur in several disjoint play items.
    // Reject menu loops by repeated time ranges, including loops mixed with a logo.
    internal static bool IsRepetitive(IEnumerable<(string Name, double In, double Out)> items)
    {
        var clips = items.ToArray();
        var total = clips.Sum(c => c.Out - c.In);
        if (total <= 0) return false;
        double unique = 0;
        foreach (var group in clips.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var end = double.NegativeInfinity;
            foreach (var clip in group.OrderBy(c => c.In))
            {
                unique += Math.Max(0, clip.Out - Math.Max(end, clip.In));
                end = Math.Max(end, clip.Out);
            }
        }
        return total - unique >= total * .5 - .001;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
