using System.Text.Json;

namespace EmbyBlurayHelper.App;

// Classification uses saved Emby data only; no IO, scheduling or probing.
internal static class LibraryMediaAnalyzer
{
    internal static LibraryMovie? Analyze(JsonElement item)
    {
        var sources = new List<JsonElement>();
        if (item.TryGetProperty("MediaSources", out var mediaSources) && mediaSources.ValueKind == JsonValueKind.Array)
            sources.AddRange(mediaSources.EnumerateArray());
        if (sources.Count == 0) sources.Add(item);
        var discSources = sources.Where(s => Disc(s) || (Text(s, "Container").Length == 0 && Disc(item))).ToArray();
        if (discSources.Length == 0) return null;
        var movie = new LibraryMovie { Id = Text(item, "Id"), Name = Text(item, "Name") };
        var noAudio = false;
        var width = 0;
        var height = 0;
        foreach (var source in discSources)
        {
            var path = Text(source, "Path");
            var entry = new LibrarySource { Id = Text(source, "Id"), Path = path, RunTimeTicks = Long(source, "RunTimeTicks"),
                IsIso = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
                        Text(source, "Container").Equals("iso", StringComparison.OrdinalIgnoreCase) ||
                        Text(source, "Container").Equals("blurayiso", StringComparison.OrdinalIgnoreCase) };
            if (source.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = Text(stream, "Type");
                    if (type == "Video")
                    {
                        width = Math.Max(width, Number(stream, "Width"));
                        height = Math.Max(height, Number(stream, "Height"));
                    }
                    if (type is not ("Audio" or "Subtitle") || Flag(stream, "IsExternal")) continue;
                    entry.Tracks.Add(new LibraryTrack { Index = Number(stream, "Index"), Type = type, Language = Text(stream, "Language") });
                }
            }
            noAudio |= !entry.Tracks.Any(t => t.Type == "Audio") || path.Length == 0;
            movie.Sources.Add(entry);
        }
        movie.Format = width >= 3840 || height >= 2160 ? "UHD" : width == 1920 || height == 1080 ? "Blu-ray" : "待确认";
        movie.MissingAudio = movie.Sources.Sum(s => s.Tracks.Count(t => t.Type == "Audio" && Unknown(t.Language)));
        movie.MissingSubtitles = movie.Sources.Sum(s => s.Tracks.Count(t => t.Type == "Subtitle" && Unknown(t.Language)));
        movie.MissingRuntime = movie.Sources.Count(source => source.RunTimeTicks is null or <= 0);
        movie.Classification = noAudio ? "unknown" : movie.MissingAudio + movie.MissingSubtitles > 0 ? "missing"
            : movie.MissingRuntime > 0 ? "unknown" : "complete";
        movie.Message = noAudio ? "Emby 尚未返回内部音轨信息，需要重新探测后确认。"
            : movie.MissingRuntime > 0 ? $"{movie.MissingRuntime} 个原盘媒体源缺少时长，需要补全。" : "";
        return movie;
    }

    private static bool Disc(JsonElement source) => Text(source, "Container").Equals("bluray", StringComparison.OrdinalIgnoreCase) ||
        Text(source, "Container").Equals("blurayiso", StringComparison.OrdinalIgnoreCase) ||
        Text(source, "Container").Equals("iso", StringComparison.OrdinalIgnoreCase) ||
        Text(source, "VideoType").Equals("Bluray", StringComparison.OrdinalIgnoreCase) ||
        Text(source, "IsoType").Equals("BluRay", StringComparison.OrdinalIgnoreCase);

    private static bool Unknown(string language) => string.IsNullOrWhiteSpace(language) || language.Trim().ToLowerInvariant() is "und" or "unk" or "unknown" or "undefined";
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static int Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) ? number : 0;
    private static long? Long(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : null;
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
