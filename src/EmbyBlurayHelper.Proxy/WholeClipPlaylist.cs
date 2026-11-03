using System.Buffers.Binary;
using System.Text;

namespace EmbyBlurayHelper.Proxy;

// A conservative raw reader for complete, single-angle, seamless M2TS clips.
// Match MPLS times against CLPI before using whole files. Partial clips and
// complex titles are deliberately left to Emby's existing playback paths.
internal sealed class WholeClipPlaylist
{
    internal sealed record Clip(string Path, long Length);
    public IReadOnlyList<Clip> Clips { get; }
    public long Length { get; }

    private WholeClipPlaylist(List<Clip> clips)
    {
        Clips = clips.AsReadOnly();
        Length = clips.Sum(c => c.Length);
    }

    public static WholeClipPlaylist? TryCreate(string root, int number, out string reason)
    {
        reason = "播放列表含裁剪、多角度、复杂时间域或非无缝连接，内置读取器无法确认完整片段边界，保留原响应。";
        var bdmv = DirectoryPath(root, "BDMV");
        var playlist = Metadata(FilePath(DirectoryPath(bdmv, "PLAYLIST"), $"{number:D5}.mpls"), "MPLS");
        var section = checked((int)U32(playlist, 8));
        var end = SectionEnd(playlist, section);
        var count = U16(playlist, section + 6);
        if (count is < 2 or > 256 || U16(playlist, section + 8) != 0) return null;
        var cursor = section + 10;
        var clips = new List<Clip>();
        var streamDirectory = DirectoryPath(bdmv, "STREAM");
        var clipDirectory = DirectoryPath(bdmv, "CLIPINF");
        for (var index = 0; index < count; index++)
        {
            var item = cursor + 2;
            var itemEnd = checked(item + U16(playlist, cursor));
            if (itemEnd > end || itemEnd - item < 32) throw new InvalidDataException("MPLS 片段数据不完整。");
            var id = Encoding.ASCII.GetString(playlist, item, 5);
            if (!id.All(char.IsAsciiDigit) || Encoding.ASCII.GetString(playlist, item + 5, 4) != "M2TS") return null;
            var flags = U16(playlist, item + 9);
            var connection = flags & 15;
            if ((flags & 0x10) != 0 || connection is not (1 or 5 or 6) || index > 0 && connection == 1) return null;
            var start = U32(playlist, item + 12);
            var stop = U32(playlist, item + 16);
            if (stop <= start) return null;
            var clip = Metadata(FilePath(clipDirectory, id + ".clpi"), "HDMV");
            if (clip[46] != 1 || clip[47] != 1) return null;
            var sequence = checked((int)U32(clip, 8));
            var sequenceEnd = SectionEnd(clip, sequence);
            // One ATC and one STC starting at packet zero: no timestamp resets
            // or alternative time domains can hide an in/out trim here.
            if (sequenceEnd - sequence < 26 || clip[sequence + 5] != 1 || U32(clip, sequence + 6) != 0 ||
                clip[sequence + 10] != 1 || clip[sequence + 11] != playlist[item + 11] ||
                U32(clip, sequence + 14) != 0) return null;
            var clipStart = U32(clip, sequence + 18);
            var clipEnd = U32(clip, sequence + 22);
            if (start != clipStart || stop < clipEnd || stop - clipEnd > 1) return null;
            var size = U32(clip, 56) * 192L;
            var path = FilePath(streamDirectory, id + ".m2ts");
            if (size <= 0 || new FileInfo(path).Length != size)
                throw new InvalidDataException($"片段 {id} 的文件大小与 CLPI 包数量不一致。");
            clips.Add(new(path, size));
            cursor = itemEnd;
        }
        reason = "已通过 MPLS / CLPI 验证完整片段，使用内置按需读取器。";
        return new(clips);
    }

    public Stream Open(long offset)
    {
        if (offset < 0 || offset >= Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var opened = new List<FileStream>();
        Span<byte> packet = stackalloc byte[192];
        try
        {
            // Validate every clip's read permission and length before sending
            // successful response headers, including clips reached after a seek.
            foreach (var clip in Clips)
            {
                var stream = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                opened.Add(stream);
                if (stream.Length != clip.Length) throw new IOException("原盘文件大小已改变，请重新检查影片。");
                stream.ReadExactly(packet);
                if (packet[4] != 0x47 || (packet[7] & 0xc0) != 0)
                    throw new InvalidDataException("内置读取器仅支持未加密的 M2TS 片段。");
                stream.Position = 0;
            }
            return new ClipStream(opened.ToArray(), offset);
        }
        catch
        {
            foreach (var stream in opened) stream.Dispose();
            throw;
        }
    }

    private static string DirectoryPath(string parent, string name) => Directory.Exists(Path.Combine(parent, name))
        ? Path.Combine(parent, name)
        : Directory.EnumerateDirectories(parent).FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase))
          ?? throw new DirectoryNotFoundException("缺少原盘目录：" + name);

    private static string FilePath(string parent, string name) => File.Exists(Path.Combine(parent, name))
        ? Path.Combine(parent, name)
        : Directory.EnumerateFiles(parent, Path.GetFileNameWithoutExtension(name) + ".*")
            .FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase))
          ?? throw new FileNotFoundException("缺少原盘文件：" + name);

    private static byte[] Metadata(string path, string signature)
    {
        if (new FileInfo(path).Length is < 64 or > 16 * 1024 * 1024) throw new InvalidDataException("原盘元数据大小无效。");
        var bytes = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(bytes, 0, 4) != signature ||
            Encoding.ASCII.GetString(bytes, 4, 4) is not ("0100" or "0200" or "0300"))
            throw new InvalidDataException("原盘元数据格式不受支持。");
        return bytes;
    }

    private static int SectionEnd(byte[] data, int start)
    {
        var end = checked(start + 4 + (int)U32(data, start));
        if (start < 40 || end > data.Length || end < start + 10) throw new InvalidDataException("原盘元数据段超出文件范围。");
        return end;
    }

    private static ushort U16(byte[] data, int offset) => offset >= 0 && offset <= data.Length - 2
        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)) : throw new InvalidDataException("原盘元数据不完整。");
    private static uint U32(byte[] data, int offset) => offset >= 0 && offset <= data.Length - 4
        ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : throw new InvalidDataException("原盘元数据不完整。");

    private sealed class ClipStream(FileStream[] streams, long offset) : Stream
    {
        private int index = SeekStart(streams, offset);
        private bool disposed;
        private static int SeekStart(FileStream[] streams, long offset)
        {
            var index = 0;
            while (offset >= streams[index].Length) offset -= streams[index++].Length;
            streams[index].Position = offset;
            return index;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (buffer.Length == 0) return 0;
            while (index < streams.Length)
            {
                var stream = streams[index];
                if (stream.Position == stream.Length) { index++; continue; }
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) throw new EndOfStreamException("原盘片段读取中断。");
                return count;
            }
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) { disposed = true; foreach (var stream in streams) stream.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
