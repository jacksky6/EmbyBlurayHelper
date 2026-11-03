namespace EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

// This executable is a supplementary reader, never Emby's upstream probe.
internal sealed record MediaFeatureOptions(bool Enabled = false, string ProbePath = "", string? CacheDirectory = null,
    MediaFeatureAttempt? Attempt = null);
