using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscUtils.Udf;
using EmbyBlurayHelper.ProbeWrapper;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

internal sealed class DvDiscPlan
{
    internal sealed record Clip(string Member, ClpiIndex Index, long InMs, long TimelineMs);
    internal sealed record Segment(int Number, Clip Clip, long Start, long End, long InputStart, long InputEnd, long ApproxStartMs, long SlotOffset, int SlotBytes);
    internal string Path { get; private init; } = "";
    internal bool Iso { get; private init; }
    internal string Signature { get; private set; } = "";
    internal string Playlist { get; private init; } = "";
    internal long DurationMs { get; private init; }
    internal byte Level { get; private init; } = 6;
    internal List<Segment> Segments { get; } = [];
    internal long Length => VirtualMatroska.HeaderBytes + Segments.Sum(s => (long)s.SlotBytes);
    internal Dictionary<int, string> Languages { get; } = [];
    internal long[] Chapters { get; private init; } = [];
    private (string Path, long Length, long Stamp)[] stamps = [];
    internal bool IsCurrent => stamps.All(s => { var file = new FileInfo(s.Path); return file.Exists && file.Length == s.Length && file.LastWriteTimeUtc.Ticks == s.Stamp; });
    internal static bool Candidate(JsonElement source)
    {
        if (!source.TryGetProperty("Path", out var value) || value.ValueKind != JsonValueKind.String) return false;
        var path = value.GetString()!; var format = source.TryGetProperty("Container", out var c) ? c.GetString() ?? "" : "";
        if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("index.bdmv", StringComparison.OrdinalIgnoreCase) && !format.Equals("bluray", StringComparison.OrdinalIgnoreCase)) return false;
        if (new[] { ".mkv", ".mp4", ".m2ts", ".ts", ".strm", ".avi", ".mov" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return false;
        return source.TryGetProperty("MediaStreams", out var streams) && streams.EnumerateArray().Any(s =>
            s.TryGetProperty("Type", out var t) && t.GetString() == "Video" && s.TryGetProperty("Width", out var w) && w.GetInt32() >= 3840 &&
            ((s.TryGetProperty("ExtendedVideoSubType", out var v) && v.GetString()?.StartsWith("DoviProfile7", StringComparison.OrdinalIgnoreCase) == true) ||
             (s.TryGetProperty("Title", out var title) && title.GetString()?.Contains("Dolby Vision Profile 7", StringComparison.OrdinalIgnoreCase) == true)));
    }
    internal static DvDiscPlan Create(JsonElement source, CancellationToken token, string engineStamp = "")
    {
        var path = source.GetProperty("Path").GetString()!; var iso = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        using var image = iso ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
        using var udf = image is null ? null : new UdfReader(image);
        var indexPath = File.Exists(path) ? path : File.Exists(System.IO.Path.Combine(path, "index.bdmv")) ? System.IO.Path.Combine(path, "index.bdmv") : System.IO.Path.Combine(path, "BDMV", "index.bdmv");
        using var metadata = udf is not null ? IsoDiscMetadata.Open(udf, token) : IsoDiscMetadata.OpenDirectory(indexPath, token);
        var selection = metadata.SelectChapterPlaylist(token, source.GetProperty("RunTimeTicks").GetInt64(), source.TryGetProperty("Size", out var size) ? size.GetInt64() : 0, iso, requireEquivalentClips: true);
        var playlist = selection.Playlist;
        if (playlist.StreamClips.Any(c => c.AngleIndex != 0))
            throw new InvalidDataException("Multi-angle Blu-ray playlists retain original playback.");
        var rate = source.GetProperty("MediaStreams").EnumerateArray().Where(s => s.TryGetProperty("Type", out var type) && type.GetString() == "Video")
            .Select(s => s.TryGetProperty("AverageFrameRate", out var value) && value.TryGetDouble(out var fps) ? fps : 0).Max();
        if (rate is <= 0 or > 60.1) throw new InvalidDataException("DV conversion requires a known UHD frame rate up to 60 fps.");
        var plan = new DvDiscPlan { Path = path, Iso = iso, Playlist = playlist.Name, DurationMs = (long)Math.Round(playlist.TotalLength * 1000),
            Level = rate <= 24.1 ? (byte)6 : rate <= 30.1 ? (byte)7 : rate <= 48.1 ? (byte)8 : (byte)9,
            Chapters = source.TryGetProperty("Chapters", out var chapters) ? chapters.EnumerateArray().Where(c => !c.TryGetProperty("MarkerType", out var marker) || marker.GetString() is null or "Chapter")
                .Select(c => c.GetProperty("StartPositionTicks").GetInt64() / 10000).Where(t => t >= 0 && t < playlist.TotalLength * 1000).Distinct().Order().ToArray() : [] };
        var stampFiles = new List<string>();
        if (iso) stampFiles.Add(path); else stampFiles.Add(Directory.EnumerateFiles(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(indexPath)!, "PLAYLIST"))
            .Single(p => System.IO.Path.GetFileName(p).Equals(playlist.Name, StringComparison.OrdinalIgnoreCase)));
        foreach (var stream in playlist.PlaylistStreams.Values.Where(s => s.IsAudioStream || s.IsTextStream || s.IsGraphicsStream))
            if (stream.LanguageCode is { Length: 3 } lang && lang.All(char.IsAsciiLetter)) plan.Languages[stream.PID] = lang.ToLowerInvariant();
        long slot = 0;
        foreach (var item in playlist.StreamClips.Where(c => c.AngleIndex == 0))
        {
            token.ThrowIfCancellationRequested(); using var clpi = item.StreamClipFile.FileInfo.OpenRead();
            if (clpi.Length > 4 * 1024 * 1024) throw new InvalidDataException("CLPI exceeds navigation budget.");
            var bytes = new byte[clpi.Length]; clpi.ReadExactly(bytes); var index = ClpiIndex.Read(bytes);
            if (Math.Abs(item.TimeIn * 45000 - index.Start45) > 1 || Math.Abs(item.TimeOut * 45000 - index.End45) > 1)
                throw new InvalidDataException("DV conversion currently requires complete Blu-ray play items; trimmed branches keep original playback.");
            var member = metadata.StreamPaths[item.Name];
            if (!iso)
            {
                stampFiles.Add(member); stampFiles.Add(item.StreamClipFile.FileInfo.FullName);
                if (new FileInfo(member).Length != index.Bytes) throw new IOException("M2TS size differs from CLPI.");
            }
            var clip = new Clip(member, index, (long)Math.Round(item.TimeIn * 1000), (long)Math.Round(item.RelativeTimeIn * 1000));
            var starts = new List<int> { 0 };
            for (var i = 1; i < index.Points.Length; i++) if (index.Points[i].Time45 - index.Points[starts[^1]].Time45 >= 4 * 45000) starts.Add(i);
            for (var n = 0; n < starts.Count; n++)
            {
                var first = starts[n]; var next = n + 1 < starts.Count ? starts[n + 1] : index.Points.Length;
                var start = index.Points[first].Offset; var end = next < index.Points.Length ? index.Points[next].Offset : index.Bytes;
                var warmup = first == 0 ? 0 : index.Points[first - 1].Offset;
                var finish = next + 2 < index.Points.Length ? index.Points[next + 2].Offset : index.Bytes;
                // Fixed virtual slots make Range addresses stable without reading
                // the movie in advance. Removed EL bytes become EBML Void padding.
                var capacity = checked((int)(end - start + 256 * 1024));
                if (finish - warmup > 192 * 1024 * 1024 || capacity > 128 * 1024 * 1024 || capacity < 1024) throw new InvalidDataException("Blu-ray GOP exceeds the bounded DV fragment budget.");
                plan.Segments.Add(new(plan.Segments.Count, clip, start, end, warmup, finish,
                    Math.Max(0, (index.Points[first].Time45 - index.Start45) / 45) + clip.TimelineMs, slot, capacity));
                slot = checked(slot + capacity);
            }
        }
        if (plan.Segments.Count is 0 or > 10000) throw new InvalidDataException("Invalid DV segment count.");
        plan.stamps = stampFiles.Distinct().Select(p => { var f = new FileInfo(p); return (f.FullName, f.Length, f.LastWriteTimeUtc.Ticks); }).ToArray();
        plan.Signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("DV-v1\n" + engineStamp + "\n" + path + "\n" + playlist.Name + "\n" + plan.DurationMs + "|" + plan.Level + "\n" +
            string.Join('\n', plan.stamps.Select(s => s.Path + "|" + s.Length + "|" + s.Stamp)) + "\n" + string.Join(',', plan.Chapters)))).ToLowerInvariant();
        return plan;
    }
    internal DvInput Open(Segment segment) => new(this, segment);
}

internal sealed class DvInput : IDisposable
{
    private readonly FileStream? image;
    private readonly UdfReader? udf;
    private readonly Stream member;
    internal Stream Slice { get; }
    internal long StartMs { get; }
    internal long EndMs { get; }
    internal DvInput(DvDiscPlan plan, DvDiscPlan.Segment segment)
    {
        try
        {
            if (!plan.IsCurrent) throw new IOException("Blu-ray changed; refresh playback information.");
            if (plan.Iso) { image = File.Open(plan.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); udf = new UdfReader(image); member = udf.OpenFile(segment.Clip.Member, FileMode.Open, FileAccess.Read); }
            else member = File.Open(segment.Clip.Member, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (member.Length != segment.Clip.Index.Bytes) throw new IOException("Blu-ray stream length changed.");
            StartMs = Pts(member, segment.Start);
            EndMs = segment.End == member.Length ? segment.Clip.Index.End45 / 45 : Pts(member, segment.End);
            member.Position = 0; var prefixBuffer = new byte[65536 / 192 * 192]; member.ReadExactly(prefixBuffer);
            using var prefix = new MemoryStream();
            var seen = new HashSet<int>(); var completed = new HashSet<int>();
            for (var i = 0; i < prefixBuffer.Length; i += 192)
            {
                var pid = (prefixBuffer[i + 5] & 31) * 256 + prefixBuffer[i + 6];
                if (pid is not (0 or 0x100) || completed.Contains(pid)) continue;
                if ((prefixBuffer[i + 5] & 64) != 0 && !seen.Add(pid)) { completed.Add(pid); continue; }
                prefix.Write(prefixBuffer, i, 192);
            }
            if (!seen.Contains(0) || !seen.Contains(0x100)) throw new InvalidDataException("Missing Blu-ray PAT/PMT preamble.");
            Slice = new SliceStream(member, prefix.ToArray(), segment.InputStart, segment.InputEnd - segment.InputStart,
                plan.Languages.ToDictionary(p => checked((ushort)p.Key), p => p.Value));
        }
        catch { Dispose(); throw; }
    }
    private static long Pts(Stream stream, long offset)
    {
        stream.Position = offset; var packet = new byte[192];
        for (var i = 0; i < 512 && stream.Position + 192 <= stream.Length; i++)
        {
            stream.ReadExactly(packet);
            if (packet[4] != 0x47 || (packet[7] & 0xc0) != 0) throw new InvalidDataException("Encrypted or malformed M2TS.");
            if ((packet[5] & 0x5f) != 0x50 || packet[6] != 0x11 || (packet[7] & 0x10) == 0) continue;
            var p = 8 + ((packet[7] & 0x20) != 0 ? 1 + packet[8] : 0);
            if (p + 14 > packet.Length || !packet.AsSpan(p, 3).SequenceEqual(new byte[] { 0, 0, 1 }) || (packet[p + 7] & 0x80) == 0) continue;
            var t = packet.AsSpan(p + 9);
            var value = ((long)(t[0] & 14) << 29) | ((long)t[1] << 22) | ((long)(t[2] & 254) << 14) | ((long)t[3] << 7) | (uint)(t[4] >> 1);
            return (value + 45) / 90;
        }
        throw new InvalidDataException("CLPI point has no base-layer PES timestamp.");
    }
    public void Dispose() { member?.Dispose(); udf?.Dispose(); image?.Dispose(); }
    private sealed class SliceStream(Stream input, byte[] prefix, long start, long length, IReadOnlyDictionary<ushort, string> languages) : Stream
    {
        private readonly byte[] buffer = new byte[TransportLanguagePatcher.BlockSize];
        private long blockOffset = -1;
        private int blockLength;
        public override long Position { get; set; }
        public override long Length => prefix.Length + length;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position < 0 || Position > Length) throw new IOException("Slice position out of bounds.");
            count = (int)Math.Min(count, Length - Position); if (count == 0) return 0;
            var block = Position / this.buffer.Length * this.buffer.Length;
            if (blockOffset != block)
            {
                blockLength = (int)Math.Min(this.buffer.Length, Length - block); var cursor = block; var filled = 0;
                while (filled < blockLength)
                {
                    int n;
                    if (cursor < prefix.Length) { n = (int)Math.Min(blockLength - filled, prefix.Length - cursor); prefix.AsSpan((int)cursor, n).CopyTo(this.buffer.AsSpan(filled)); }
                    else { input.Position = start + cursor - prefix.Length; n = input.Read(this.buffer, filled, blockLength - filled); if (n == 0) throw new EndOfStreamException(); }
                    filled += n; cursor += n;
                }
                TransportLanguagePatcher.PatchBlock(this.buffer.AsSpan(0, blockLength), languages); blockOffset = block;
            }
            var within = (int)(Position - blockOffset); count = Math.Min(count, blockLength - within);
            this.buffer.AsSpan(within, count).CopyTo(buffer.AsSpan(offset));
            Position += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentException() };
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
