using MediaBrowser.Model.Configuration;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

// Emby owns the on/off switch, schedule, interval and storage policy. There is
// deliberately no second thumbnail switch or timer in Helper configuration.
internal static class DiscThumbnailPolicy
{
    internal const string NativeTaskKey = "RefreshChapterImages";
    internal static bool Enabled(LibraryOptions options, bool duringScan) =>
        options.EnableChapterImageExtraction && (!duringScan || options.ExtractChapterImagesDuringLibraryScan);
    internal static int Interval(LibraryOptions options) => options.ThumbnailImagesIntervalSeconds == -1
        ? -1 : Math.Min(300, Math.Max(1, options.ThumbnailImagesIntervalSeconds));
}
