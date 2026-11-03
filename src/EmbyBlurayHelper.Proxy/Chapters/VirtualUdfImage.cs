using System.Buffers.Binary;
using System.Text;

namespace EmbyBlurayHelper.Proxy.Chapters;

// A read-only UDF image assembled from small metadata buffers and source-file
// extents. No movie is copied, mounted, or materialized on disk.
internal sealed class VirtualUdfImage
{
    internal const int Sector = 2048;
    internal const int FormatRevision = 2;
    private const uint Partition = 288;
    internal sealed record ImageFile(string Name, string? SourcePath, long Length, byte[]? Bytes = null);
    private sealed class Node(string name, Node? parent, ImageFile? file)
    {
        internal string Name = name;
        internal Node? Parent = parent;
        internal ImageFile? File = file;
        internal List<Node> Children = [];
        internal uint Entry, Data;
        internal long Length;
    }
    private sealed record Extent(long Start, long Length, byte[]? Bytes, string? Path);
    private readonly List<Extent> extents = [];
    internal long Length { get; private set; }
    internal int MetadataBytes => extents.Where(e => e.Bytes is not null).Sum(e => e.Bytes!.Length);
    internal IEnumerable<(long Offset, long Length, bool Metadata)> Regions => extents.Select(e => (e.Start, e.Length, e.Bytes is not null));

    internal static VirtualUdfImage Create(IEnumerable<ImageFile> files)
    {
        var image = new VirtualUdfImage();
        var root = new Node("", null, null);
        var nodes = new List<Node> { root };
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (file.Length < 0 || file.Bytes is not null && file.Bytes.LongLength != file.Length ||
                (file.SourcePath is null) == (file.Bytes is null)) throw new InvalidDataException("Invalid image file extent.");
            var parts = file.Name.Replace('\\', '/').Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || Encoding.UTF8.GetByteCount(p) > 120))
                throw new InvalidDataException("Invalid UDF file name.");
            var parent = root;
            foreach (var part in parts[..^1])
            {
                var next = parent.Children.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (next is null) { next = new(part, parent, null); parent.Children.Add(next); nodes.Add(next); }
                if (next.File is not null) throw new InvalidDataException("Image path conflicts with a file.");
                parent = next;
            }
            if (parent.Children.Any(n => n.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Duplicate image path.");
            var leaf = new Node(parts[^1], parent, file) { Length = file.Length };
            parent.Children.Add(leaf); nodes.Add(leaf);
        }
        if (nodes.Count > 20000) throw new InvalidDataException("Too many files in virtual disc.");
        uint cursor = 1; // Metadata partition block zero contains the file set descriptor.
        foreach (var node in nodes) node.Entry = cursor++;
        foreach (var node in nodes.Where(n => n.File is null))
        {
            node.Length = Fid(node.Parent ?? node, "", 10, 0).Length + node.Children.Sum(n => Fid(n, n.Name, n.File is null ? (byte)2 : (byte)0, 0).Length);
            node.Data = cursor; cursor = checked(cursor + Blocks(node.Length));
        }
        var metadataBlocks = Align(cursor, 32);
        const uint metadataDataStart = 32;
        cursor = checked(metadataDataStart + metadataBlocks);
        foreach (var node in nodes.Where(n => n.File is not null)) { node.Data = cursor; cursor = checked(cursor + Blocks(node.Length)); }
        var mirrorEntry = Align(cursor, 32);
        var mirrorDataStart = checked(mirrorEntry + 32);
        var partitionBlocks = checked(mirrorDataStart + metadataBlocks);
        var end = checked(Partition + partitionBlocks + 287);
        var reserveDescriptorStart = checked(end - 287);
        image.Length = checked(((long)end + 1) * Sector);
        image.Put(16, Recognition("BEA01")); image.Put(17, Recognition("NSR03")); image.Put(18, Recognition("TEA01"));
        foreach (var anchor in new uint[] { 256, end - 256, end })
        {
            var b = new byte[Sector]; U32(b, 16, 16 * Sector); U32(b, 20, 32); U32(b, 24, 16 * Sector); U32(b, 28, reserveDescriptorStart);
            Tag(b, 2, anchor, 512); image.Put(anchor, b);
        }
        foreach (var start in new uint[] { 32, reserveDescriptorStart })
        {
            var pvd = new byte[Sector]; U32(pvd, 16, 1); DString(pvd, 24, 32, "BDAREA"); U16(pvd, 56, 1); U16(pvd, 58, 1);
            // These are character-set bitmaps, not a sector count. Stricter
            // readers (including Windows UDF) reject unsupported set bits.
            U16(pvd, 60, 2); U16(pvd, 62, 3); U32(pvd, 64, 1); U32(pvd, 68, 1); DString(pvd, 72, 128, "0000000000000000EmbyBlurayHelper");
            Charset(pvd, 200); Charset(pvd, 264); Entity(pvd, 344, "*EmbyBlurayHelper"); Timestamp(pvd, 376); Entity(pvd, 388, "*EmbyBlurayHelper");
            Tag(pvd, 1, start, 512); image.Put(start, pvd);
            var pd = new byte[Sector]; U32(pd, 16, 2); U16(pd, 20, 1); Entity(pd, 24, "+NSR03");
            U32(pd, 184, 1); U32(pd, 188, Partition); U32(pd, 192, partitionBlocks); Entity(pd, 196, "*EmbyBlurayHelper");
            Tag(pd, 5, start + 1, 512); image.Put(start + 1, pd);
            var lvd = new byte[Sector]; U32(lvd, 16, 3); Charset(lvd, 20); DString(lvd, 84, 128, "EMBY_BLURAY"); U32(lvd, 212, Sector);
            Entity(lvd, 216, "*OSTA UDF Compliant", 0x0250); LongAd(lvd, 248, Sector, 0, 1); U32(lvd, 264, 70); U32(lvd, 268, 2);
            Entity(lvd, 272, "*EmbyBlurayHelper"); U32(lvd, 432, 16 * Sector); U32(lvd, 436, 64);
            lvd[440] = 1; lvd[441] = 6; U16(lvd, 442, 1); U16(lvd, 444, 0);
            lvd[446] = 2; lvd[447] = 64; Entity(lvd, 450, "*UDF Metadata Partition", 0x0250);
            U16(lvd, 482, 1); U16(lvd, 484, 0); U32(lvd, 486, 0); U32(lvd, 490, mirrorEntry); U32(lvd, 494, uint.MaxValue);
            U32(lvd, 498, 32); U16(lvd, 502, 32); lvd[504] = 1;
            Tag(lvd, 6, start + 2, 510); image.Put(start + 2, lvd);
            var usd = new byte[Sector]; U32(usd, 16, 4); Tag(usd, 7, start + 3, 24); image.Put(start + 3, usd);
            var td = new byte[Sector]; Tag(td, 8, start + 4, 512); image.Put(start + 4, td);
        }
        // This immutable image is a closed volume (1), never an open write session (0).
        var integrity = new byte[Sector]; Timestamp(integrity, 16); U32(integrity, 28, 1); U64(integrity, 40, (ulong)nodes.Count + 16);
        U32(integrity, 72, 2); U32(integrity, 76, 46); U32(integrity, 80, 0); U32(integrity, 84, 0);
        U32(integrity, 88, partitionBlocks); U32(integrity, 92, metadataBlocks);
        Entity(integrity, 96, "*EmbyBlurayHelper"); U32(integrity, 128, (uint)nodes.Count(n => n.File is not null));
        U32(integrity, 132, (uint)nodes.Count(n => n.File is null)); U16(integrity, 136, 0x0250); U16(integrity, 138, 0x0250); U16(integrity, 140, 0x0250);
        Tag(integrity, 9, 64, 142); image.Put(64, integrity);
        var iterm = new byte[Sector]; Tag(iterm, 8, 65, 512); image.Put(65, iterm);
        var fsd = new byte[Sector]; Timestamp(fsd, 16); U16(fsd, 28, 3); U16(fsd, 30, 3); U32(fsd, 32, 1); U32(fsd, 36, 1);
        Charset(fsd, 48); DString(fsd, 112, 128, "EMBY_BLURAY"); Charset(fsd, 240); DString(fsd, 304, 32, "EmbyBlurayHelper");
        LongAd(fsd, 400, Sector, root.Entry, 1); Entity(fsd, 416, "*OSTA UDF Compliant", 0x0250); Tag(fsd, 256, 0, 512);
        image.Put(Partition + metadataDataStart, fsd);
        image.Put(Partition, MetadataFileEntry(250, 0, metadataDataStart, metadataBlocks, 1));
        image.Put(Partition + mirrorEntry, MetadataFileEntry(251, mirrorEntry, mirrorDataStart, metadataBlocks, 2));
        ulong unique = 16;
        foreach (var node in nodes)
        {
            var b = new byte[Sector]; U16(b, 20, 4); U16(b, 24, 1); b[27] = node.File is null ? (byte)4 : (byte)5;
            U32(b, 28, node.Parent?.Entry ?? root.Entry); U16(b, 32, 1); U16(b, 34, node.File is null ? (ushort)0 : (ushort)1); U32(b, 36, uint.MaxValue); U32(b, 40, uint.MaxValue);
            U32(b, 44, 0x14a5); U16(b, 48, (ushort)(node.File is null ? 1 + node.Children.Count(n => n.File is null) : 1));
            U64(b, 56, (ulong)node.Length); U64(b, 64, Blocks(node.Length)); Timestamp(b, 72); Timestamp(b, 84); Timestamp(b, 96);
            U32(b, 108, 1); Entity(b, 128, "*EmbyBlurayHelper"); U64(b, 160, unique++);
            var remaining = node.Length; var block = node.Data; var ad = 176;
            while (remaining > 0)
            {
                var length = (uint)Math.Min(remaining, 0x3ffff800L);
                var descriptorLength = node.File is null ? 8 : 16;
                if (ad + descriptorLength > Sector) throw new InvalidDataException("Virtual disc file exceeds allocation descriptor capacity.");
                if (node.File is null) { U32(b, ad, length); U32(b, ad + 4, block); }
                else LongAd(b, ad, length, block, 0);
                block = checked(block + Blocks(length)); remaining -= length; ad += descriptorLength;
            }
            U32(b, 172, (uint)(ad - 176)); Tag(b, 261, node.Entry, ad); image.Put(Partition + metadataDataStart + node.Entry, b);
            if (node.File is null)
            {
                using var directory = new MemoryStream();
                directory.Write(Fid(node.Parent ?? node, "", 10, node.Data));
                foreach (var child in node.Children) directory.Write(Fid(child, child.Name, child.File is null ? (byte)2 : (byte)0, node.Data + (uint)(directory.Position / Sector)));
                image.Put(Partition + metadataDataStart + node.Data, directory.ToArray());
            }
            else if (node.Length > 0) image.extents.Add(new(((long)Partition + node.Data) * Sector, node.Length, node.File.Bytes, node.File.SourcePath));
        }
        var primaryMetadataStart = (long)(Partition + metadataDataStart) * Sector;
        var mirrorOffset = (long)(mirrorDataStart - metadataDataStart) * Sector;
        foreach (var extent in image.extents.Where(e => e.Bytes is not null && e.Start >= primaryMetadataStart && e.Start < primaryMetadataStart + (long)metadataBlocks * Sector).ToArray())
            image.extents.Add(new(extent.Start + mirrorOffset, extent.Length, extent.Bytes, null));
        image.extents.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < image.extents.Count; i++) if (image.extents[i - 1].Start + image.extents[i - 1].Length > image.extents[i].Start)
                throw new InvalidDataException("Overlapping virtual disc extents.");
        return image;
    }

    internal Stream Open() => new ImageStream(this);
    private void Put(uint block, byte[] bytes) => extents.Add(new((long)block * Sector, bytes.Length, bytes, null));
    private static uint Blocks(long length) => checked((uint)((length + Sector - 1) / Sector));
    private static uint Align(uint value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static void U16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void U32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void U64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
    private static void LongAd(byte[] b, int o, uint length, uint block, ushort partition = 0) { U32(b, o, length); U32(b, o + 4, block); U16(b, o + 8, partition); }
    private static byte[] MetadataFileEntry(byte fileType, uint entry, uint dataStart, uint blocks, ulong uniqueId)
    {
        var length = checked(blocks * Sector); var b = new byte[224];
        U16(b, 20, 4); U16(b, 24, 1); b[27] = fileType; U32(b, 28, entry); U32(b, 36, uint.MaxValue); U32(b, 40, uint.MaxValue);
        U32(b, 44, 0x14a5); U16(b, 48, 1); U64(b, 56, length); U64(b, 64, length); U64(b, 72, blocks);
        Timestamp(b, 80); Timestamp(b, 92); Timestamp(b, 104); Timestamp(b, 116); U32(b, 128, 1);
        Entity(b, 168, "*EmbyBlurayHelper"); U64(b, 200, uniqueId); U32(b, 212, 8); U32(b, 216, length); U32(b, 220, dataStart);
        Tag(b, 266, entry, b.Length); return b;
    }
    private static byte[] Recognition(string name) { var b = new byte[Sector]; Encoding.ASCII.GetBytes(name).CopyTo(b, 1); b[6] = 1; return b; }
    private static void Charset(byte[] b, int o) => Encoding.ASCII.GetBytes("OSTA Compressed Unicode").CopyTo(b, o + 1);
    private static void Entity(byte[] b, int o, string name, ushort revision = 0) { Encoding.ASCII.GetBytes(name).CopyTo(b, o + 1); U16(b, o + 24, revision); }
    private static void DString(byte[] b, int o, int size, string value) { b[o] = 8; var n = Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, size - 2)), b.AsSpan(o + 1)); b[o + size - 1] = (byte)(n + 1); }
    private static void Timestamp(byte[] b, int o) { U16(b, o, 0x1000); U16(b, o + 2, 2026); b[o + 4] = 1; b[o + 5] = 1; }
    private static byte[] Fid(Node node, string name, byte flags, uint location)
    {
        var text = name.Length == 0 ? [] : Encoding.BigEndianUnicode.GetBytes(name);
        var n = text.Length == 0 ? 0 : text.Length + 1; var b = new byte[(38 + n + 3) & ~3];
        U16(b, 16, 1); b[18] = flags; b[19] = (byte)n; LongAd(b, 20, Sector, node.Entry, 1);
        if (n > 0) { b[38] = 16; text.CopyTo(b, 39); }
        Tag(b, 257, location, b.Length); return b;
    }
    private static void Tag(byte[] b, ushort id, uint block, int size)
    {
        U16(b, 0, id); U16(b, 2, 3); U16(b, 6, 1); U16(b, 10, (ushort)(size - 16)); U32(b, 12, block);
        ushort crc = 0; foreach (var v in b.AsSpan(16, size - 16)) { crc ^= (ushort)(v << 8); for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1); }
        U16(b, 8, crc); byte sum = 0; for (var i = 0; i < 16; i++) if (i != 4) sum += b[i]; b[4] = sum;
    }

    private sealed class ImageStream(VirtualUdfImage image) : Stream
    {
        private long position;
        private FileStream? source; private string? sourcePath;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => image.Length;
        public override long Position { get => position; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); position = value; } }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var total = (int)Math.Min(buffer.Length, Math.Max(0, Length - position)); var written = 0;
            while (written < total)
            {
                var extent = image.extents.FirstOrDefault(e => e.Start + e.Length > position);
                var n = (int)Math.Min(total - written, extent is null ? Length - position : position < extent.Start ? extent.Start - position : extent.Start + extent.Length - position);
                var target = buffer.Slice(written, n);
                if (extent is null || position < extent.Start) target.Clear();
                else if (extent.Bytes is not null) extent.Bytes.AsSpan((int)(position - extent.Start), n).CopyTo(target);
                else
                {
                    if (sourcePath != extent.Path)
                    {
                        source?.Dispose(); source = new(extent.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess); sourcePath = extent.Path;
                        if (source.Length != extent.Length) throw new IOException("Original disc file size changed.");
                    }
                    source!.Position = position - extent.Start; source.ReadExactly(target);
                }
                position += n; written += n;
            }
            return total;
        }
        public override long Seek(long offset, SeekOrigin origin) { Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset); return position; }
        protected override void Dispose(bool disposing) { if (disposing) source?.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
