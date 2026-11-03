using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin;

internal static class StreamMerge
{
    public static List<MediaStream> Merge(IEnumerable<MediaStream> probed, IEnumerable<MediaStream>? saved)
    {
        var result = probed.Where(stream => !stream.IsExternal).ToList();
        var existing = saved?.ToArray() ?? Array.Empty<MediaStream>();
        if (result.Select(stream => stream.Index).Distinct().Count() != result.Count)
            throw new InvalidOperationException("The probe returned duplicate stream indexes; saved streams were left unchanged.");

        foreach (var old in existing.Where(stream => !stream.IsExternal &&
                     (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Subtitle)))
        {
            var match = result.SingleOrDefault(stream => stream.Index == old.Index && stream.Type == old.Type &&
                string.Equals(stream.Codec, old.Codec, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new InvalidOperationException("An existing audio/subtitle stream could not be matched; saved streams were left unchanged.");
            if (!Unknown(old.Language)) match.Language = old.Language;
            if (!string.IsNullOrWhiteSpace(old.Title)) match.Title = old.Title;
        }

        var nextIndex = result.Count == 0 ? 0 : result.Max(stream => stream.Index) + 1;
        foreach (var external in existing.Where(stream => stream.IsExternal))
        {
            if (result.Any(stream => stream.Index == external.Index)) external.Index = nextIndex++;
            else nextIndex = Math.Max(nextIndex, external.Index + 1);
            result.Add(external);
        }
        return result;
    }

    private static bool Unknown(string? language) => string.IsNullOrWhiteSpace(language) ||
        new[] { "und", "unk", "unknown", "undefined" }.Contains(language!.Trim(), StringComparer.OrdinalIgnoreCase);
}
