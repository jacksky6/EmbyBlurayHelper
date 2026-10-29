using System.Text.Json;
using BDInfo;

namespace EmbyBlurayHelper.ProbeWrapper;

internal static class BluRayLanguageAugmenter
{
    public static bool ShouldCapture(IReadOnlyList<string> arguments)
    {
        if (!HasJsonOutput(arguments))
        {
            return false;
        }

        var inputPath = NormalizeInputPath(FindInputPath(arguments));
        return !string.IsNullOrWhiteSpace(inputPath) && IsBluRayPath(inputPath);
    }

    public static byte[] TryPatch(byte[] output, IReadOnlyList<string> arguments)
    {
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        var inputPath = NormalizeInputPath(FindInputPath(arguments));
        var formatFilename = NormalizeInputPath(FindFormatFilename(document.RootElement));
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            inputPath = formatFilename;
        }

        var playlistMatched = false;
        var languageByPid = string.IsNullOrWhiteSpace(inputPath)
            ? new Dictionary<ushort, string>()
            : LoadLanguages(inputPath, out playlistMatched);
        if (languageByPid.Count == 0 && !playlistMatched &&
            !string.Equals(inputPath, formatFilename, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(formatFilename))
        {
            languageByPid = LoadLanguages(formatFilename, out _);
        }
        if (languageByPid.Count == 0)
        {
            return output;
        }

        using var patchedOutput = new MemoryStream(output.Length + 256);
        using (var writer = new Utf8JsonWriter(patchedOutput))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
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
        IReadOnlyDictionary<ushort, string> languageByPid)
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
        IReadOnlyDictionary<ushort, string> languageByPid,
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

        return languageByPid.TryGetValue(pid, out language!);
    }

    private static Dictionary<ushort, string> LoadLanguages(string inputPath, out bool playlistMatched)
    {
        var playlistLanguages = LoadPlaylistLanguages(inputPath, out playlistMatched);
        if (playlistMatched)
        {
            return playlistLanguages;
        }

        var clipInfoPaths = ResolveClipInfoPaths(inputPath);
        var candidates = new Dictionary<ushort, HashSet<string>>();

        foreach (var clipInfoPath in clipInfoPaths)
        {
            try
            {
                var clipInfo = new TSStreamClipFile(
                    new BDInfo.IO.FileInfo(new FileInfo(clipInfoPath)));
                clipInfo.Scan();
                foreach (var stream in clipInfo.Streams.Values)
                {
                    if (string.IsNullOrWhiteSpace(stream.LanguageCode) ||
                        IsUnknownLanguage(stream.LanguageCode))
                    {
                        continue;
                    }

                    if (!candidates.TryGetValue(stream.PID, out var languages))
                    {
                        languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        candidates.Add(stream.PID, languages);
                    }

                    languages.Add(stream.LanguageCode);
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

    private static Dictionary<ushort, string> LoadPlaylistLanguages(string inputPath, out bool playlistMatched)
    {
        playlistMatched = false;
        var streamPaths = ExtractConcatPaths(inputPath);
        if (streamPaths.Count == 0)
        {
            return new Dictionary<ushort, string>();
        }

        var bdmvDirectory = FindBdmvDirectory(Path.GetDirectoryName(streamPaths[0]));
        if (bdmvDirectory is null)
        {
            return new Dictionary<ushort, string>();
        }

        try
        {
            var bdrom = new BDROM(new BDInfo.IO.DirectoryInfo(Directory.GetParent(bdmvDirectory)!.FullName));
            EnsureDiscIndexes(bdrom);
            var expectedClips = streamPaths
                .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant() + ".M2TS")
                .ToArray();
            var languagesByPid = new Dictionary<ushort, HashSet<string>>();

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
                            languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            languagesByPid.Add(stream.PID, languages);
                        }

                        languages.Add(stream.LanguageCode ?? string.Empty);
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
                    .Where(pair => pair.Value.Count == 1 && !IsUnknownLanguage(pair.Value.Single()))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Single());
            }
        }
        catch
        {
            // Fall back to CLPI when playlist metadata cannot be read.
        }

        return new Dictionary<ushort, string>();
    }

    private static void EnsureDiscIndexes(BDROM bdrom)
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
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("-i", StringComparison.OrdinalIgnoreCase) &&
                arguments[index].Length > 2)
            {
                return arguments[index][2..];
            }
        }

        return null;
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

        return path;
    }

    private static bool IsBluRayPath(string path)
    {
        return path.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".clpi", StringComparison.OrdinalIgnoreCase) ||
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
}
