using System.Text.Json;
using BDInfo;
using DiscUtils.Udf;

namespace EmbyBlurayHelper.ProbeWrapper;

internal static class BluRayLanguageAugmenter
{
    internal static string? GetMediaInput(IReadOnlyList<string> arguments) => NormalizeInputPath(FindInputPath(arguments));
    internal static IReadOnlyDictionary<ushort, DiscLanguage> ReadIsoLanguageMap(string isoPath, string? playlistName = null) =>
        LoadIsoLanguages(Path.GetFullPath(isoPath), isoPath, out _, playlistName);

    internal static string? GetIsoInput(IReadOnlyList<string> arguments) =>
        FindIsoPath(NormalizeInputPath(FindInputPath(arguments)) ?? string.Empty);

    internal static string? GetBdmvIndexInput(IReadOnlyList<string> arguments)
    {
        var path = NormalizeInputPath(FindInputPath(arguments));
        return path is not null && Path.GetFileName(path).Equals("index.bdmv", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "BDMV", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? path : null;
    }

    public static bool ShouldCapture(IReadOnlyList<string> arguments)
    {
        if (!HasJsonOutput(arguments))
        {
            return false;
        }

        var inputPath = NormalizeInputPath(FindInputPath(arguments));
        return !string.IsNullOrWhiteSpace(inputPath) &&
               (IsBluRayPath(inputPath) || FindIsoPath(inputPath) is not null);
    }

    public static byte[] TryPatch(byte[] output, IReadOnlyList<string> arguments) =>
        TryPatch(output, arguments, out _);

    public static byte[] TryPatch(byte[] output, IReadOnlyList<string> arguments, out string? diagnostic,
        string? playlistName = null)
    {
        diagnostic = null;
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return output;
        }

        var hasStreams = document.RootElement.TryGetProperty("streams", out var streams) &&
                         streams.ValueKind == JsonValueKind.Array;

        var inputPath = NormalizeInputPath(FindInputPath(arguments));
        var rawFormatFilename = FindFormatFilename(document.RootElement);
        var formatFilename = NormalizeInputPath(rawFormatFilename);
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            inputPath = formatFilename;
        }
        var originalIsoPath = rawFormatFilename?.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase) == true
            ? FindIsoPath(inputPath ?? string.Empty)
            : null;

        var playlistMatched = false;
        var languageByPid = string.IsNullOrWhiteSpace(inputPath)
            ? new Dictionary<ushort, DiscLanguage>()
            : LoadLanguages(inputPath, out playlistMatched, playlistName);
        if (languageByPid.Count == 0 && !playlistMatched &&
            !string.Equals(inputPath, formatFilename, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(formatFilename))
        {
            languageByPid = LoadLanguages(formatFilename, out playlistMatched, playlistName);
        }
        if (languageByPid.Count == 0)
        {
            diagnostic = $"Blu-ray language metadata: no unambiguous audio/subtitle language found for {SafeFileName(inputPath ?? "input")}.";
            if (originalIsoPath is null) return output;
        }

        diagnostic ??= DescribeProbeStreams(document.RootElement, languageByPid, playlistMatched, inputPath);
        if (!hasStreams)
        {
            return output;
        }

        using var patchedOutput = new MemoryStream(output.Length + 256);
        using (var writer = new Utf8JsonWriter(patchedOutput))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("format") && originalIsoPath is not null &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("format");
                    writer.WriteStartObject();
                    foreach (var formatProperty in property.Value.EnumerateObject())
                    {
                        if (formatProperty.NameEquals("filename"))
                        {
                            writer.WriteString("filename", originalIsoPath);
                        }
                        else
                        {
                            formatProperty.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                    continue;
                }

                if (!property.NameEquals("streams"))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("streams");
                writer.WriteStartArray();
                foreach (var stream in streams.EnumerateArray())
                {
                    WritePatchedStream(writer, stream, languageByPid);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return patchedOutput.ToArray();
    }

    private static void WritePatchedStream(
        Utf8JsonWriter writer,
        JsonElement stream,
        IReadOnlyDictionary<ushort, DiscLanguage> languageByPid)
    {
        var shouldPatch = TryGetStreamLanguage(stream, languageByPid, out var language);
        var hasTags = false;
        var hasLanguage = false;

        writer.WriteStartObject();
        foreach (var property in stream.EnumerateObject())
        {
            if (!property.NameEquals("tags"))
            {
                property.WriteTo(writer);
                continue;
            }

            hasTags = true;
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                if (shouldPatch)
                {
                    writer.WriteStartObject("tags");
                    writer.WriteString("language", language);
                    writer.WriteEndObject();
                    hasLanguage = true;
                }
                else
                {
                    property.WriteTo(writer);
                }

                continue;
            }

            writer.WritePropertyName("tags");
            writer.WriteStartObject();
            foreach (var tag in property.Value.EnumerateObject())
            {
                if (shouldPatch && tag.NameEquals("language") &&
                    (tag.Value.ValueKind != JsonValueKind.String ||
                     IsUnknownLanguage(tag.Value.GetString())))
                {
                    writer.WriteString("language", language);
                    hasLanguage = true;
                    continue;
                }

                tag.WriteTo(writer);
                if (tag.NameEquals("language") &&
                    tag.Value.ValueKind == JsonValueKind.String &&
                    !IsUnknownLanguage(tag.Value.GetString()))
                {
                    hasLanguage = true;
                }
            }

            if (shouldPatch && !hasLanguage)
            {
                writer.WriteString("language", language);
            }

            writer.WriteEndObject();
        }

        if (shouldPatch && !hasTags)
        {
            writer.WriteStartObject("tags");
            writer.WriteString("language", language);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static bool TryGetStreamLanguage(
        JsonElement stream,
        IReadOnlyDictionary<ushort, DiscLanguage> languageByPid,
        out string language)
    {
        language = string.Empty;
        if (!stream.TryGetProperty("codec_type", out var codecType) ||
            (codecType.GetString() is not "audio" and not "subtitle") ||
            !stream.TryGetProperty("id", out var id) ||
            !TryParsePid(id, out var pid))
        {
            return false;
        }

        if (languageByPid.TryGetValue(pid, out var detected))
        {
            language = detected.Language;
            return true;
        }

        return false;
    }

    private static Dictionary<ushort, DiscLanguage> LoadLanguages(string inputPath, out bool playlistMatched,
        string? playlistName = null)
    {
        var playlistLanguages = LoadPlaylistLanguages(inputPath, out playlistMatched);
        if (playlistMatched)
        {
            return playlistLanguages;
        }

        if (FindIsoPath(inputPath) is { } isoPath)
        {
            return LoadIsoLanguages(isoPath, inputPath, out playlistMatched, playlistName);
        }

        var clipInfoPaths = ResolveClipInfoPaths(inputPath);
        return LoadClipLanguages(clipInfoPaths);
    }

    private static Dictionary<ushort, DiscLanguage> LoadClipLanguages(IEnumerable<string> clipInfoPaths)
    {
        var candidates = new Dictionary<ushort, HashSet<DiscLanguage>>();

        foreach (var clipInfoPath in clipInfoPaths)
        {
            try
            {
                var clipInfo = new TSStreamClipFile(
                    new BDInfo.IO.FileInfo(new FileInfo(clipInfoPath)));
                clipInfo.Scan();
                foreach (var stream in clipInfo.Streams.Values)
                {
                    if (string.IsNullOrWhiteSpace(stream.LanguageCode) || IsUnknownLanguage(stream.LanguageCode) ||
                        (!stream.IsAudioStream && !stream.IsTextStream && !stream.IsGraphicsStream))
                    {
                        continue;
                    }

                    if (!candidates.TryGetValue(stream.PID, out var languages))
                    {
                        languages = [];
                        candidates.Add(stream.PID, languages);
                    }

                    languages.Add(new DiscLanguage(StreamKind(stream), stream.LanguageCode.ToLowerInvariant()));
                }
            }
            catch
            {
                // A damaged or encrypted CLPI must not prevent normal ffprobe output.
            }
        }

        return candidates
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Single(), EqualityComparer<ushort>.Default);
    }

    private static Dictionary<ushort, DiscLanguage> LoadPlaylistLanguages(string inputPath, out bool playlistMatched)
    {
        playlistMatched = false;
        var streamPaths = ExtractConcatPaths(inputPath);
        if (streamPaths.Count == 0)
        {
            return new Dictionary<ushort, DiscLanguage>();
        }

        var bdmvDirectory = FindBdmvDirectory(Path.GetDirectoryName(streamPaths[0]));
        if (bdmvDirectory is null)
        {
            return new Dictionary<ushort, DiscLanguage>();
        }

        try
        {
            var bdrom = new BDROM(new BDInfo.IO.DirectoryInfo(Directory.GetParent(bdmvDirectory)!.FullName));
            EnsureDiscIndexes(bdrom);
            var expectedClips = streamPaths
                .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant() + ".M2TS")
                .ToArray();
            var languagesByPid = new Dictionary<ushort, HashSet<DiscLanguage>>();

            foreach (var clipName in expectedClips)
            {
                var clipInfoName = Path.ChangeExtension(clipName, ".CLPI");
                if (bdrom.StreamClipFiles.TryGetValue(clipInfoName, out var clipInfo))
                {
                    clipInfo.Scan();
                }
            }

            foreach (var playlist in bdrom.PlaylistFiles.Values)
            {
                try
                {
                    playlist.Scan(bdrom.StreamFiles, bdrom.StreamClipFiles);
                    var actualClips = playlist.StreamClips
                        .Select(clip => clip.Name)
                        .ToArray();
                    if (!actualClips.SequenceEqual(expectedClips, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    playlistMatched = true;
                    foreach (var stream in playlist.PlaylistStreams.Values.Where(
                                 stream => stream.IsAudioStream || stream.IsTextStream || stream.IsGraphicsStream))
                    {
                        if (!languagesByPid.TryGetValue(stream.PID, out var languages))
                        {
                            languages = [];
                            languagesByPid.Add(stream.PID, languages);
                        }

                        if (!string.IsNullOrWhiteSpace(stream.LanguageCode) && !IsUnknownLanguage(stream.LanguageCode))
                        {
                            languages.Add(new DiscLanguage(StreamKind(stream), stream.LanguageCode.ToLowerInvariant()));
                        }
                    }
                }
                catch
                {
                    // Ignore malformed alternate playlists and continue searching.
                }
            }

            if (playlistMatched)
            {
                return languagesByPid
                    .Where(pair => pair.Value.Count == 1)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Single());
            }
        }
        catch
        {
            // Fall back to CLPI when playlist metadata cannot be read.
        }

        return new Dictionary<ushort, DiscLanguage>();
    }

    private static Dictionary<ushort, DiscLanguage> LoadIsoLanguages(
        string isoPath,
        string inputPath,
        out bool playlistMatched,
        string? playlistName = null)
    {
        playlistMatched = false;
        var tempRoot = Path.Combine(Path.GetTempPath(), "emby-bluray-helper-iso-" + Guid.NewGuid().ToString("N"));
        var bdmvRoot = Path.Combine(tempRoot, "BDMV");
        var playlistDirectory = Path.Combine(bdmvRoot, "PLAYLIST");
        var clipInfoDirectory = Path.Combine(bdmvRoot, "CLIPINF");
        var streamDirectory = Path.Combine(bdmvRoot, "STREAM");
        Directory.CreateDirectory(playlistDirectory);
        Directory.CreateDirectory(clipInfoDirectory);
        Directory.CreateDirectory(streamDirectory);

        try
        {
            using (var image = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var udf = new UdfReader(image))
            {
                if (!udf.DirectoryExists("\\BDMV"))
                {
                    return new Dictionary<ushort, DiscLanguage>();
                }

                CopyUdfFiles(udf, "\\BDMV\\PLAYLIST", "*.mpls", playlistDirectory);
                CopyUdfFiles(udf, "\\BDMV\\CLIPINF", "*.clpi", clipInfoDirectory);
                if (udf.FileExists("\\BDMV\\index.bdmv"))
                {
                    CopyUdfFile(udf, "\\BDMV\\index.bdmv", Path.Combine(bdmvRoot, "index.bdmv"));
                }

                foreach (var streamPath in udf.GetFiles("\\BDMV\\STREAM", "*.m2ts", SearchOption.TopDirectoryOnly))
                {
                    using var placeholder = File.Create(Path.Combine(streamDirectory, SafeFileName(streamPath)));
                }
            }

            if (playlistName is not null)
            {
                // Accept only a local playlist basename reported by ffprobe.
                if (playlistName.Length != 10 || !playlistName[..5].All(char.IsAsciiDigit) ||
                    !playlistName.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<ushort, DiscLanguage>();
                var bdrom = new BDROM(new BDInfo.IO.DirectoryInfo(tempRoot));
                EnsureDiscIndexes(bdrom);
                if (!bdrom.PlaylistFiles.TryGetValue(playlistName.ToUpperInvariant(), out var playlist))
                    return new Dictionary<ushort, DiscLanguage>();
                foreach (var clip in bdrom.StreamClipFiles.Values) clip.Scan();
                playlist.Scan(bdrom.StreamFiles, bdrom.StreamClipFiles);
                playlistMatched = true;
                return playlist.PlaylistStreams.Values
                    .Where(stream => stream.IsAudioStream || stream.IsTextStream || stream.IsGraphicsStream)
                    .Where(stream => !IsUnknownLanguage(stream.LanguageCode))
                    .GroupBy(stream => stream.PID)
                    .Where(group => group.Select(stream => new DiscLanguage(StreamKind(stream), stream.LanguageCode.ToLowerInvariant())).Distinct().Count() == 1)
                    .ToDictionary(group => group.Key, group => new DiscLanguage(StreamKind(group.First()), group.First().LanguageCode.ToLowerInvariant()));
            }

            var concatPaths = ExtractConcatPaths(inputPath);
            if (concatPaths.Count > 0)
            {
                var tempConcat = "concat:" + string.Join('|', concatPaths.Select(path =>
                    Path.Combine(streamDirectory, SafeFileName(path))));
                var playlistLanguages = LoadPlaylistLanguages(tempConcat, out playlistMatched);
                if (playlistMatched)
                {
                    return playlistLanguages;
                }
            }

            return LoadClipLanguages(Directory.EnumerateFiles(clipInfoDirectory, "*.clpi"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void CopyUdfFiles(UdfReader udf, string sourceDirectory, string pattern, string targetDirectory)
    {
        if (!udf.DirectoryExists(sourceDirectory))
        {
            return;
        }

        foreach (var sourcePath in udf.GetFiles(sourceDirectory, pattern, SearchOption.TopDirectoryOnly))
        {
            CopyUdfFile(udf, sourcePath, Path.Combine(targetDirectory, SafeFileName(sourcePath)));
        }
    }

    internal static void CopyUdfFile(UdfReader udf, string sourcePath, string targetPath)
    {
        const long maxMetadataFileSize = 64 * 1024 * 1024;
        if (udf.GetFileLength(sourcePath) > maxMetadataFileSize)
        {
            return;
        }

        using var source = udf.OpenFile(sourcePath, FileMode.Open, FileAccess.Read);
        using var target = File.Create(targetPath);
        source.CopyTo(target);
    }

    private static string SafeFileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static string DescribeProbeStreams(
        JsonElement root,
        IReadOnlyDictionary<ushort, DiscLanguage> languageByPid,
        bool playlistMatched,
        string? inputPath)
    {
        var audio = new List<string>();
        var subtitles = new List<string>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecType) ||
                    codecType.ValueKind != JsonValueKind.String ||
                    codecType.GetString() is not ("audio" or "subtitle"))
                {
                    continue;
                }

                var kind = codecType.GetString()!;
                var index = stream.TryGetProperty("index", out var indexValue) ? indexValue.ToString() : "?";
                var pidText = stream.TryGetProperty("id", out var id) ? id.ToString() : "?";
                var detail = $"#{index} PID={pidText}";
                if (id.ValueKind != JsonValueKind.Undefined && TryParsePid(id, out var pid) && languageByPid.TryGetValue(pid, out var detected))
                {
                    var existing = stream.TryGetProperty("tags", out var tags) &&
                                   tags.ValueKind == JsonValueKind.Object &&
                                   tags.TryGetProperty("language", out var languageTag) &&
                                   languageTag.ValueKind == JsonValueKind.String
                        ? languageTag.GetString()
                        : null;
                    detail += $" 原盘={detected.Kind}:{detected.Language}" +
                              (IsUnknownLanguage(existing) ? "（已补全）" : $"（保留现有 {existing}）");
                }
                else
                {
                    detail += " 原盘语言未匹配";
                }

                (kind == "audio" ? audio : subtitles).Add(detail);
            }
        }

        var metadataAudio = languageByPid.Where(pair => pair.Value.Kind == "audio")
            .Select(pair => $"0x{pair.Key:X4}={pair.Value.Language}").ToArray();
        var metadataSubtitles = languageByPid.Where(pair => pair.Value.Kind == "subtitle")
            .Select(pair => $"0x{pair.Key:X4}={pair.Value.Language}").ToArray();
        var hasProbeTracks = audio.Count + subtitles.Count > 0;
        return $"Blu-ray language probe {SafeFileName(inputPath ?? "input")} ({(playlistMatched ? "playlist" : "CLPI")}); " +
               $"audio: {(hasProbeTracks ? string.Join(", ", audio.DefaultIfEmpty("none")) : "no ffprobe streams; metadata " + string.Join(", ", metadataAudio.DefaultIfEmpty("none")))}; " +
               $"subtitles: {(hasProbeTracks ? string.Join(", ", subtitles.DefaultIfEmpty("none")) : "no ffprobe streams; metadata " + string.Join(", ", metadataSubtitles.DefaultIfEmpty("none")))}.";
    }

    private static string StreamKind(TSStream stream) =>
        stream.IsAudioStream ? "audio" : "subtitle";

    private static string? FindIsoPath(string inputPath)
    {
        var candidates = ExtractConcatPaths(inputPath).Append(inputPath);
        foreach (var candidate in candidates)
        {
            var extensionIndex = candidate.IndexOf(".iso", StringComparison.OrdinalIgnoreCase);
            if (extensionIndex < 0)
            {
                continue;
            }

            var isoPath = candidate[..(extensionIndex + 4)].Trim('"');
            if (File.Exists(isoPath))
            {
                return isoPath;
            }
        }

        return null;
    }

    internal static void EnsureDiscIndexes(BDROM bdrom)
    {
        if (bdrom.DirectoryPLAYLIST is not null && bdrom.PlaylistFiles.Count == 0)
        {
            foreach (var path in Directory.EnumerateFiles(bdrom.DirectoryPLAYLIST.FullName))
            {
                if (!Path.GetExtension(path).Equals(".mpls", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileInfo = BDInfo.IO.FileInfo.FromFullName(path);
                bdrom.PlaylistFiles.Add(Path.GetFileName(path).ToUpperInvariant(), new TSPlaylistFile(bdrom, fileInfo));
            }
        }

        if (bdrom.DirectorySTREAM is not null && bdrom.StreamFiles.Count == 0)
        {
            foreach (var path in Directory.EnumerateFiles(bdrom.DirectorySTREAM.FullName))
            {
                if (!Path.GetExtension(path).Equals(".m2ts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileInfo = BDInfo.IO.FileInfo.FromFullName(path);
                bdrom.StreamFiles.Add(Path.GetFileName(path).ToUpperInvariant(), new TSStreamFile(fileInfo));
            }
        }

        if (bdrom.DirectoryCLIPINF is not null && bdrom.StreamClipFiles.Count == 0)
        {
            foreach (var path in Directory.EnumerateFiles(bdrom.DirectoryCLIPINF.FullName))
            {
                if (!Path.GetExtension(path).Equals(".clpi", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileInfo = BDInfo.IO.FileInfo.FromFullName(path);
                bdrom.StreamClipFiles.Add(Path.GetFileName(path).ToUpperInvariant(), new TSStreamClipFile(fileInfo));
            }
        }
    }

    private static IReadOnlyList<string> ExtractConcatPaths(string inputPath)
    {
        if (!inputPath.StartsWith("concat:", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var concatValue = inputPath[7..].Trim().Trim('"');
        return concatValue
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeInputPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    private static IEnumerable<string> ResolveClipInfoPaths(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            var extension = Path.GetExtension(inputPath);
            if (extension.Equals(".clpi", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { inputPath };
            }

            if (extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase))
            {
                var bdmv = FindBdmvDirectory(Path.GetDirectoryName(inputPath));
                if (bdmv is not null)
                {
                    return new[]
                    {
                        Path.Combine(bdmv, "CLIPINF", Path.GetFileNameWithoutExtension(inputPath) + ".clpi")
                    };
                }
            }

            return Array.Empty<string>();
        }

        if (!Directory.Exists(inputPath))
        {
            return Array.Empty<string>();
        }

        var bdmvDirectory = FindBdmvDirectory(inputPath);
        if (bdmvDirectory is null)
        {
            return Array.Empty<string>();
        }

        var clipInfoDirectory = Path.Combine(bdmvDirectory, "CLIPINF");
        if (!Directory.Exists(clipInfoDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(clipInfoDirectory)
            .Where(path => Path.GetExtension(path).Equals(".clpi", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string? FindBdmvDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            if (directory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        var child = Path.Combine(path, "BDMV");
        return Directory.Exists(child) ? child : null;
    }

    private static string? FindInputPath(IReadOnlyList<string> arguments)
    {
        var index = FindInputArgumentIndex(arguments);
        if (index < 0)
        {
            return null;
        }

        return arguments[index].Equals("-i", StringComparison.OrdinalIgnoreCase)
            ? arguments[index + 1]
            : arguments[index][2..];
    }

    internal static int FindInputArgumentIndex(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("-i", StringComparison.OrdinalIgnoreCase) &&
                arguments[index].Length > 2)
            {
                return index;
            }
        }

        return -1;
    }

    private static string? FindFormatFilename(JsonElement root)
    {
        return root.TryGetProperty("format", out var format) &&
               format.TryGetProperty("filename", out var filename) &&
               filename.ValueKind == JsonValueKind.String
            ? filename.GetString()
            : null;
    }

    private static bool HasJsonOutput(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument =>
            argument.Equals("json", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("json=", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeInputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim().Trim('"');
        if (path.StartsWith("concat:", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..].Trim('"');
        }
        else if (path.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[7..].Trim('"');
        }

        return path;
    }

    private static bool IsBluRayPath(string path)
    {
        return path.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".clpi", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".iso\\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".iso/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\BDMV\\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/BDMV/", StringComparison.OrdinalIgnoreCase) ||
               Directory.Exists(Path.Combine(path, "BDMV"));
    }

    private static bool TryParsePid(JsonElement id, out ushort pid)
    {
        pid = 0;
        if (id.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = id.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out pid);
        }

        return ushort.TryParse(value, out pid);
    }

    private static bool IsUnknownLanguage(string? language)
    {
        return string.IsNullOrWhiteSpace(language) ||
               language.Equals("und", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record DiscLanguage(string Kind, string Language);
}
