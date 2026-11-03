using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin.MediaFeatures;

// An existing library item keeps all original technical fields, languages,
// user titles, stream order and external subtitles. Only confirmed extras merge.
internal static class FeatureStreamMerge
{
    public static List<MediaStream> Merge(IEnumerable<MediaStream> current, IEnumerable<MediaStream> probed)
    {
        var streams = current.ToList();
        var candidates = probed.Where(s => !s.IsExternal).ToArray();
        foreach (var saved in streams.Where(s => !s.IsExternal))
        {
            var matches = candidates.Where(s => s.Index == saved.Index && s.Type == saved.Type &&
                string.Equals(s.Codec, saved.Codec, StringComparison.OrdinalIgnoreCase) &&
                (s.Type != MediaStreamType.Audio || s.Channels == saved.Channels)).ToArray();
            if (matches.Length != 1) continue;
            var detected = matches[0];
            if (saved.Type == MediaStreamType.Audio && detected.Profile?.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (saved.Codec.Equals("truehd", StringComparison.OrdinalIgnoreCase) || saved.Codec.Equals("eac3", StringComparison.OrdinalIgnoreCase)))
                saved.Profile = detected.Profile;
            if (saved.Type == MediaStreamType.Video && detected.ExtendedVideoType == ExtendedVideoTypes.DolbyVision)
            {
                saved.ExtendedVideoType = detected.ExtendedVideoType;
                saved.ExtendedVideoSubType = detected.ExtendedVideoSubType;
            }
            if (string.IsNullOrWhiteSpace(saved.Title) && !string.IsNullOrWhiteSpace(detected.Title) &&
                (detected.Title.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0 || detected.Title.IndexOf("Dolby Vision", StringComparison.OrdinalIgnoreCase) >= 0))
                saved.Title = detected.Title;
        }
        return streams;
    }
}
