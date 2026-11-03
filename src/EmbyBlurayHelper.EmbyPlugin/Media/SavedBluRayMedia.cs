using System.Globalization;
using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin.Media;

internal sealed class SavedBluRayMedia
{
    public List<MediaStream> Streams { get; }
    public long? RunTimeTicks { get; }
    public long Size { get; }
    public SavedBluRayMedia(List<MediaStream> streams, long? runtime, long size)
    { Streams = streams; RunTimeTicks = runtime; Size = size; }
    public string RuntimeDisplay => RunTimeTicks > 0
        ? TimeSpan.FromTicks(RunTimeTicks.Value).ToString("c", CultureInfo.InvariantCulture) : "unknown";
    public string AudioLanguages => Languages(MediaStreamType.Audio);
    public string SubtitleLanguages => Languages(MediaStreamType.Subtitle);
    private string Languages(MediaStreamType type) => string.Join(", ", Streams.Where(s => s.Type == type)
        .OrderBy(s => s.Index).Select(s => $"#{s.Index}={s.Language ?? "und"}"));
    private static object Summary(MediaStream stream) => new
    { index = stream.Index, language = stream.Language, codec = stream.Codec, title = stream.Title };
    public object Response(string itemId, string sourceId) => new
    {
        itemId, sourceId, streamCount = Streams.Count, runTimeTicks = RunTimeTicks, size = Size,
        audio = Streams.Where(s => s.Type == MediaStreamType.Audio).Select(Summary).ToArray(),
        subtitles = Streams.Where(s => s.Type == MediaStreamType.Subtitle).Select(Summary).ToArray()
    };
}
