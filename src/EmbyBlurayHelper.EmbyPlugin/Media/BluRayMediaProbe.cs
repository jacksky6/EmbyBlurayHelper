using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace EmbyBlurayHelper.EmbyPlugin.Media;

internal static class BluRayMediaProbe
{
    public static async Task<MediaInfo> Probe(IMediaProbeManager probe, MediaSourceInfo source, CancellationToken token)
    {
        // An isolated probe target bypasses Emby's legacy BDInfo mount/refresh path.
        // Playback and the library's saved source path remain the original directory.
        var target = source;
        if (Directory.Exists(source.Path))
        {
            var bdmv = Path.GetFileName(source.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("BDMV", StringComparison.OrdinalIgnoreCase) ? source.Path : Path.Combine(source.Path, "BDMV");
            var index = Path.Combine(bdmv, "index.bdmv");
            if (!File.Exists(index)) throw new InvalidOperationException("BDMV/index.bdmv was not found; no media information was changed.");
            target = new MediaSourceInfo { Id = source.Id, Path = index, Protocol = MediaProtocol.File, Container = "mpegts" };
        }
        var media = await probe.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = target, MediaType = DlnaProfileType.Video, ExtractChapters = false
        }, token).ConfigureAwait(false);
        if (media?.MediaStreams is null ||
            !media.MediaStreams.Any(s => s.Type == MediaStreamType.Video) ||
            !media.MediaStreams.Any(s => s.Type == MediaStreamType.Audio))
            throw new InvalidOperationException("Emby probing returned no complete Blu-ray stream list; cached data was left unchanged.");
        return media;
    }
}
