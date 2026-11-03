using System.Buffers.Binary;
using DiscUtils.Udf;

namespace EmbyBlurayHelper.ProbeWrapper.Thumbnails;

// Emby still performs decoding, tone mapping and image encoding with its own
// configured extractor. This process only exposes seekable, read-only inputs.
internal sealed class DiscThumbnailSession : IAsyncDisposable
{
    private FileStream? image;
    private UdfReader? udf;
    private IsoDiscMetadata? metadata;
    private readonly List<(Stream Stream, IsoMediaHttpSource Server)> sources = [];
    internal object Description { get; private set; } = null!;
    internal sealed record Clip(string Name, string InputPath, long TimelineStartTicks, long TimelineEndTicks, long OffsetTicks);

    internal static async Task<DiscThumbnailSession> Open(string path, long runtime, long size, CancellationToken token)
    {
        var session = new DiscThumbnailSession();
        try
        {
            var stamp = DiscChapterReader.Stamp(path);
            var iso = Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase);
            if (iso)
            {
                session.image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                session.udf = new UdfReader(session.image);
                session.metadata = IsoDiscMetadata.Open(session.udf, token);
            }
            else
            {
                var index = File.Exists(path) ? path : File.Exists(Path.Combine(path, "index.bdmv"))
                    ? Path.Combine(path, "index.bdmv") : Path.Combine(path, "BDMV", "index.bdmv");
                session.metadata = IsoDiscMetadata.OpenDirectory(index, token);
            }
            var selection = session.metadata.SelectChapterPlaylist(token, runtime, size, iso);
            if (runtime > 0 && selection.Match is not ("exact-clip-size-and-runtime" or "runtime-and-equivalent-chapters"))
                throw new InvalidDataException("原盘正片与 Emby 时长不匹配，未提取截图。");
            var clips = new List<Clip>();
            var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sharedLock = new object();
            foreach (var clip in selection.Playlist.StreamClips.Where(c => c.AngleIndex == 0))
            {
                token.ThrowIfCancellationRequested();
                // The native extractor seeks relative to the file's PTS origin.
                // Refuse multiple clock domains instead of attaching wrong frames.
                using var clpi = clip.StreamClipFile.FileInfo.OpenRead();
                var header = new byte[40]; clpi.ReadExactly(header);
                clpi.Position = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                var sequence = new byte[26]; clpi.ReadExactly(sequence);
                if (sequence[5] != 1 || sequence[10] != 1)
                    throw new InvalidDataException("原盘包含多个时钟域，暂不提取截图，避免时间错位。");
                var origin = BinaryPrimitives.ReadUInt32BigEndian(sequence.AsSpan(18)) / 45000.0;
                if (!inputs.TryGetValue(clip.Name, out var input))
                {
                    input = session.metadata.StreamPaths[clip.Name];
                    if (iso)
                    {
                        var stream = session.udf!.OpenFile(input, FileMode.Open, FileAccess.Read);
                        var server = new IsoMediaHttpSource(stream, token, sharedLock, long.MaxValue);
                        session.sources.Add((stream, server)); input = server.Url;
                    }
                    inputs.Add(clip.Name, input);
                }
                clips.Add(new Clip(clip.Name, input, Ticks(clip.RelativeTimeIn), Ticks(clip.RelativeTimeOut), Ticks(clip.TimeIn - origin)));
            }
            if (clips.Count == 0 || clips.Count > 1000 || clips.Any(c => c.OffsetTicks < -TimeSpan.TicksPerSecond || c.TimelineEndTicks <= c.TimelineStartTicks))
                throw new InvalidDataException("原盘正片的片段时间无效。");
            if (stamp != DiscChapterReader.Stamp(path)) throw new IOException("原盘读取期间发生变化。");
            session.Description = new { Schema = 1, Path = path, SourceStamp = stamp, Playlist = selection.Playlist.Name,
                DurationTicks = Ticks(selection.Playlist.TotalLength), Clips = clips };
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }
    private static long Ticks(double seconds) => checked((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    public async ValueTask DisposeAsync()
    {
        foreach (var (stream, server) in sources) { await server.DisposeAsync(); stream.Dispose(); }
        metadata?.Dispose(); udf?.Dispose(); image?.Dispose();
    }
}
