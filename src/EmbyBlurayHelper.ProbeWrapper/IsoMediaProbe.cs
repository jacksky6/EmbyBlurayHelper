using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BDInfo;
using DiscUtils.Udf;
using EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

namespace EmbyBlurayHelper.ProbeWrapper;

internal sealed record IsoProbeResult(byte[] Output, string PlaylistName);

// Probes real elementary streams with Emby's original ffprobe. UDF/MPLS supplies
// disc navigation, duration and language, never guessed codec/HDR/channel data.
internal static class IsoMediaProbe
{
    internal static async Task<IsoProbeResult> Run(string executablePath, IReadOnlyList<string> arguments,
        string isoPath, string? selectedPlaylist, CancellationToken token, MediaFeatureOptions? mediaFeatures = null)
    {
        using var featureAttempt = MediaFeatureAttempt.Start(mediaFeatures, isoPath);
        mediaFeatures = featureAttempt?.Options ?? mediaFeatures;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var image = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var udf = new UdfReader(image);
        using var metadata = IsoDiscMetadata.Open(udf, timeout.Token);
        var playlist = metadata.SelectPlaylist(timeout.Token, selectedPlaylist ?? RequestedPlaylist(arguments));
        return await ProbePlaylist(executablePath, arguments, isoPath, image.Length, metadata, playlist,
            name => udf.OpenFile(metadata.StreamPaths[name], FileMode.Open, FileAccess.Read), timeout.Token, mediaFeatures);
    }

    internal static async Task<IsoProbeResult> RunDirectory(string executablePath, IReadOnlyList<string> arguments,
        string indexPath, CancellationToken token, MediaFeatureOptions? mediaFeatures = null)
    {
        using var featureAttempt = MediaFeatureAttempt.Start(mediaFeatures, indexPath);
        mediaFeatures = featureAttempt?.Options ?? mediaFeatures;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var metadata = IsoDiscMetadata.OpenDirectory(indexPath, timeout.Token);
        TSPlaylistFile playlist;
        try { playlist = metadata.SelectPlaylist(timeout.Token, RequestedPlaylist(arguments)); }
        catch (InvalidDataException) when (metadata.StreamPaths.Count == 1 && RequestedPlaylist(arguments) is null)
        {
            // A remux may carry unusable navigation and a single intact M2TS.
            // With exactly one member there is no title/clip choice to guess.
            var path = metadata.StreamPaths.Values.Single();
            using var output = new MemoryStream();
            var exit = await ProbeProcess.RunProcessAsync(executablePath, PrepareArguments(arguments, path), output, timeout.Token);
            if (exit != 0 || !ProbeProcess.HasVideo(output.ToArray()))
                throw new InvalidDataException("BDMV navigation is unreadable and its sole M2TS could not be probed.");
            var enriched = await MediaFeatureEnricher.Enrich(output.ToArray(), path, FileIdentity(path), mediaFeatures, timeout.Token);
            var result = JsonNode.Parse(enriched)!.AsObject();
            if (!result["streams"]!.AsArray().Any(s => s?["codec_type"]?.ToString() == "audio") ||
                !double.TryParse(result["format"]?["duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                !double.IsFinite(duration) || duration <= 0)
                throw new InvalidDataException("BDMV sole M2TS has no complete audio/video/duration information.");
            result["format"]!["filename"] = indexPath;
            Console.Error.WriteLine("EmbyBlurayHelper.ProbeWrapper: BDMV navigation unreadable; used its sole M2TS with real ffprobe duration/language tags. No language was inferred.");
            return new(Encoding.UTF8.GetBytes(result.ToJsonString()), string.Empty);
        }
        return await ProbePlaylist(executablePath, arguments, indexPath,
            metadata.StreamPaths.Values.Sum(p => new FileInfo(p).Length), metadata, playlist,
            name => File.OpenRead(metadata.StreamPaths[name]), timeout.Token, mediaFeatures);
    }

    private static async Task<IsoProbeResult> ProbePlaylist(string executablePath, IReadOnlyList<string> arguments,
        string inputPath, long sourceSize, IsoDiscMetadata metadata, TSPlaylistFile playlist,
        Func<string, Stream> openMember, CancellationToken token, MediaFeatureOptions? mediaFeatures)
    {
        var clips = playlist.StreamClips.Where(c => c.AngleIndex == 0)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(c => c.Length).First())
            .OrderByDescending(c => c.Length).ToArray();
        var expectedPids = playlist.PlaylistStreams.Values
            .Where(s => s.IsVideoStream || s.IsAudioStream || s.StreamType == TSStreamType.PRESENTATION_GRAPHICS || s.IsTextStream)
            .Select(s => (int)s.PID).ToHashSet();
        var collected = new Dictionary<int, JsonObject[]>();
        JsonObject? result = null;
        long readBytes = 0;
        foreach (var clip in clips)
        {
            token.ThrowIfCancellationRequested();
            // Additional clips are read only when their CLPI can supply a missing PID.
            if (collected.Count > 0 && !clip.StreamClipFile.Streams.Keys.Any(pid => expectedPids.Contains(pid) && !collected.ContainsKey(pid))) continue;
            using var member = openMember(clip.Name);
            await using var source = new IsoMediaHttpSource(member, token);
            using var output = new MemoryStream();
            var exitCode = await ProbeProcess.RunProcessAsync(executablePath,
                PrepareArguments(arguments, source.Url), output, token);
            if (exitCode != 0 || !ProbeProcess.HasVideo(output.ToArray()))
                throw new InvalidDataException($"ISO M2TS probe failed ({clip.Name}, ffprobe exit {exitCode}); no replacement media information was saved.");
            var identity = FileIdentity(inputPath) + "|" + playlist.Name + "|" + clip.Name + "|" + member.Length +
                (member is FileStream file ? "|" + FileIdentity(file.Name) : "");
            var enriched = await MediaFeatureEnricher.Enrich(output.ToArray(), source.Url, identity, mediaFeatures, token);
            readBytes += source.BytesRead;
            var probed = JsonNode.Parse(enriched)!.AsObject();
            result ??= probed;
            foreach (var group in probed["streams"]!.AsArray().OfType<JsonObject>()
                         .Where(s => Pid(s) is { } pid && (expectedPids.Contains(pid) || s["codec_type"]?.ToString() == "video"))
                         .GroupBy(s => Pid(s)!.Value))
            {
                // Preserve ffprobe's DV enhancement video and HD/core audio pairs.
                // Different codecs sharing a PID are real streams, not duplicates.
                if (!collected.ContainsKey(group.Key))
                    collected.Add(group.Key, group.Select(s => (JsonObject)s.DeepClone()).ToArray());
            }
            if (expectedPids.All(collected.ContainsKey)) break;
        }
        if (result is null || !collected.Values.SelectMany(s => s).Any(s => s["codec_type"]?.GetValue<string>() == "video") ||
            !collected.Values.SelectMany(s => s).Any(s => s["codec_type"]?.GetValue<string>() == "audio"))
            throw new InvalidDataException("ISO M2TS probe did not return the playlist's video/audio streams.");
        var missingPids = expectedPids.Where(pid => !collected.ContainsKey(pid)).ToArray();
        if (missingPids.Length > 0)
            throw new InvalidDataException("ISO M2TS probe is incomplete; missing playlist PID(s): " + string.Join(", ", missingPids.Select(p => $"0x{p:X4}")));

        var duration = playlist.TotalLength.ToString("0.000000", CultureInfo.InvariantCulture);
        var streams = new JsonArray();
        foreach (var stream in collected.Values.SelectMany(s => s))
        {
            stream["index"] = streams.Count;
            if (Pid(stream) is { } pid && playlist.PlaylistStreams.TryGetValue((ushort)pid, out var entry) &&
                stream["codec_type"]?.ToString() is "audio" or "subtitle" &&
                !string.IsNullOrWhiteSpace(entry.LanguageCode) && entry.LanguageCode != "und" &&
                (string.IsNullOrWhiteSpace(stream["tags"]?["language"]?.ToString()) || stream["tags"]?["language"]?.ToString() == "und"))
            {
                if (stream["tags"] is not JsonObject) stream["tags"] = new JsonObject();
                stream["tags"]!["language"] = entry.LanguageCode.ToLowerInvariant();
            }
            // Clip timestamps and duration do not describe the complete playlist.
            foreach (var property in new[] { "start_pts", "start_time", "duration_ts", "duration", "nb_frames" }) stream.Remove(property);
            streams.Add(stream);
        }
        result["streams"] = streams;
        result.Remove("programs"); // Program copies carry old indices/clip timing.
        result.Remove("error");
        var format = result["format"] as JsonObject ?? new JsonObject();
        result["format"] = format;
        format["nb_streams"] = streams.Count;
        format["filename"] = inputPath;
        format["start_time"] = "0.000000";
        format["duration"] = duration;
        format["size"] = sourceSize.ToString(CultureInfo.InvariantCulture);
        // ISO size includes menus/extras, so it is not a feature bitrate.
        format.Remove("bit_rate");
        if (arguments.Any(a => a == "-show_chapters"))
        {
            var chapters = playlist.Chapters.Where(c => c >= 0 && c < playlist.TotalLength).Distinct().Order().ToArray();
            result["chapters"] = new JsonArray(chapters.Select((c, i) => (JsonNode)new JsonObject
            {
                ["id"] = i, ["time_base"] = "1/1000000", ["start"] = (long)Math.Round(c * 1000000),
                ["start_time"] = c.ToString("0.000000", CultureInfo.InvariantCulture),
                ["end"] = (long)Math.Round((i + 1 < chapters.Length ? chapters[i + 1] : playlist.TotalLength) * 1000000),
                ["end_time"] = (i + 1 < chapters.Length ? chapters[i + 1] : playlist.TotalLength).ToString("0.000000", CultureInfo.InvariantCulture)
            }).ToArray());
        }
        else result.Remove("chapters");
        Console.Error.WriteLine($"EmbyBlurayHelper.ProbeWrapper: {(inputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? "ISO UDF/M2TS" : "BDMV/M2TS")} reader succeeded; {streams.Count} streams, " +
            $"duration {duration}s, source size {sourceSize}; read {readBytes} media bytes. No mounting, full extraction or transcoding.");
        foreach (var (type, label) in new[] { ("audio", "audio"), ("subtitle", "subtitles") })
            Console.Error.WriteLine($"EmbyBlurayHelper.ProbeWrapper: {playlist.Name} {label}: " +
                string.Join(", ", streams.OfType<JsonObject>().Where(s => s["codec_type"]?.ToString() == type)
                    .Select(s => $"#{s["index"]} PID {s["id"]}={s["tags"]?["language"]?.ToString() ?? "und (not marked)"}")));
        return new(Encoding.UTF8.GetBytes(result.ToJsonString()), playlist.Name);
    }

    private static string FileIdentity(string path)
    {
        var file = new FileInfo(path);
        return file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }

    internal static string? RequestedPlaylist(IReadOnlyList<string> arguments)
    {
        for (var i = arguments.Count - 2; i >= 0; i--)
        {
            if (arguments[i] != "-playlist") continue;
            if (!int.TryParse(arguments[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 99999)
                throw new ArgumentException("ISO playlist must be a number from 0 to 99999.");
            return number.ToString("D5", CultureInfo.InvariantCulture) + ".MPLS";
        }
        return null;
    }

    internal static int? Pid(JsonObject stream)
    {
        var value = stream["id"]?.ToString();
        return int.TryParse(value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? value[2..] : value,
            value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    internal static IReadOnlyList<string> PrepareArguments(IReadOnlyList<string> arguments, string url)
    {
        var inputIndex = BluRayLanguageAugmenter.FindInputArgumentIndex(arguments);
        if (inputIndex < 0) throw new ArgumentException("ISO probe has no input argument.");
        var rewritten = arguments.ToList();
        var separateInput = rewritten[inputIndex].Equals("-i", StringComparison.OrdinalIgnoreCase);
        rewritten[separateInput ? inputIndex + 1 : inputIndex] = separateInput ? url : "-i" + url;
        // These options belong to libbluray or would filter away tracks we must verify.
        for (var i = rewritten.Count - 2; i >= 0; i--)
            if (rewritten[i] is "-playlist" or "-angle" or "-chapter" or "-f" or "-select_streams" or "-show_entries")
                rewritten.RemoveRange(i, 2);
        // A caller's protocol whitelist/blacklist is deliberately retained.
        rewritten.AddRange(["-f", "mpegts", "-show_streams", "-show_format"]);
        return rewritten;
    }
}
