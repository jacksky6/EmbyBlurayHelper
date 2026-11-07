using System.Buffers.Binary;
using System.Text;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

internal sealed class MatroskaFragment
{
    internal sealed record Track(int Number, int Type, string Codec, byte[] Definition, byte[] Private, int Width);
    internal sealed record Packet(int Track, long TimeMs, bool Key, byte Flags, ReadOnlyMemory<byte> Data, long? Duration = null);
    internal List<Track> Tracks { get; } = [];
    internal List<Packet> Packets { get; } = [];
    internal static string ParseTracksSignature(byte[] tracks) => string.Join('|', Ebml.Read(Ebml.Read(tracks).Single().Data).Where(t => t.Id == 0xae).Select(t =>
        string.Join(',', Ebml.Read(t.Data).Where(e => e.Id is 0xd7 or 0x83 or 0x86 or 0x63a2 or 0xe0 or 0xe1).Select(e => e.Id.ToString("x") + ":" + System.Convert.ToHexString(e.Data.Span)))));
    internal static MatroskaFragment Parse(ReadOnlyMemory<byte> bytes)
    {
        var document = new MatroskaFragment();
        var segment = Ebml.Read(bytes).Single(e => e.Id == 0x18538067);
        long scale = 1000000;
        foreach (var element in Ebml.Read(segment.Data))
        {
            if (element.Id == 0x1549a966)
                foreach (var value in Ebml.Read(element.Data)) if (value.Id == 0x2ad7b1) scale = checked((long)Ebml.UInt(value.Data));
            if (element.Id == 0x1654ae6b)
                foreach (var t in Ebml.Read(element.Data).Where(e => e.Id == 0xae))
                {
                    var values = Ebml.Read(t.Data).ToArray();
                    int N(uint id) => checked((int)Ebml.UInt(values.FirstOrDefault(e => e.Id == id).Data));
                    var video = values.FirstOrDefault(e => e.Id == 0xe0);
                    document.Tracks.Add(new(N(0xd7), N(0x83), Encoding.ASCII.GetString(values.Single(e => e.Id == 0x86).Data.Span),
                        t.Data.ToArray(), values.FirstOrDefault(e => e.Id == 0x63a2).Data.ToArray(),
                        video.Data.IsEmpty ? 0 : (int)Ebml.UInt(Ebml.Read(video.Data).FirstOrDefault(e => e.Id == 0xb0).Data)));
                }
            if (element.Id == 0x1f43b675)
            {
                long time = 0;
                foreach (var block in Ebml.Read(element.Data))
                {
                    if (block.Id == 0xe7) time = checked((long)Ebml.UInt(block.Data));
                    if (block.Id == 0xa3) Accept(block.Data, time, null, true);
                    if (block.Id == 0xa0)
                    {
                        var group = Ebml.Read(block.Data).ToArray();
                        var duration = group.FirstOrDefault(e => e.Id == 0x9b);
                        if (group.Any(e => e.Id is not (0xa1 or 0x9b or 0xfb)))
                            throw new InvalidDataException("Unsupported Matroska block additions.");
                        Accept(group.Single(e => e.Id == 0xa1).Data, time, duration.Data.IsEmpty ? null : checked((long)Ebml.UInt(duration.Data) * scale / 1000000), !group.Any(e => e.Id == 0xfb));
                    }
                }
            }
        }
        return document;
        void Accept(ReadOnlyMemory<byte> block, long time, long? duration, bool simpleKey)
        {
            var offset = 0; var track = checked((int)Ebml.Vint(block.Span, ref offset));
            if (block.Length < offset + 3) throw new InvalidDataException("Truncated Matroska block.");
            var relative = BinaryPrimitives.ReadInt16BigEndian(block.Span[offset..]); var flags = block.Span[offset + 2];
            if ((flags & 6) != 0) throw new InvalidDataException("Laced source fragments are unsupported.");
            document.Packets.Add(new(track, checked((time + relative) * scale / 1000000), simpleKey && (flags & 0x80) != 0, flags,
                block[(offset + 3)..], duration));
        }
    }
    internal (byte[] Tracks, List<Packet> Packets, int Video, int Rpus) Convert(DoviNative dovi, byte level = 6)
    {
        var videos = Tracks.Where(t => t.Type == 1).OrderByDescending(t => t.Width).ToArray();
        if (videos.Length is < 1 or > 2 || videos.Any(t => t.Codec != "V_MPEGH/ISO/HEVC") || videos[0].Width < 3840)
            throw new InvalidDataException("Expected one UHD HEVC base layer and optional HEVC enhancement layer.");
        var bl = videos[0]; var el = videos.ElementAtOrDefault(1);
        if (bl.Private.Length < 23 || el is not null && el.Private.Length < 23) throw new InvalidDataException("Missing HEVC codec configuration.");
        var rpus = new Dictionary<long, Queue<ReadOnlyMemory<byte>>>();
        if (el is not null)
            foreach (var packet in Packets.Where(p => p.Track == el.Number))
                foreach (var nal in Nals(packet.Data, (el.Private[21] & 3) + 1))
                    if ((nal.Span[0] >> 1 & 63) == 62)
                    {
                        if (!rpus.TryGetValue(packet.TimeMs, out var queue)) rpus[packet.TimeMs] = queue = new();
                        queue.Enqueue(nal);
                    }
        var converted = new List<Packet>(); var count = 0;
        foreach (var packet in Packets)
        {
            if (el is not null && packet.Track == el.Number) continue;
            if (packet.Track != bl.Number) { converted.Add(packet); continue; }
            var copyNals = new List<ReadOnlyMemory<byte>>();
            ReadOnlyMemory<byte> rpu = default;
            foreach (var nal in Nals(packet.Data, (bl.Private[21] & 3) + 1))
            {
                var type = nal.Span[0] >> 1 & 63;
                var layer = ((nal.Span[0] & 1) << 5) | (nal.Span[1] >> 3);
                if (type == 62) { rpu = nal; continue; }
                if (layer != 0 || type == 63) continue;
                copyNals.Add(nal);
            }
            if (rpu.IsEmpty)
            {
                if (!rpus.TryGetValue(packet.TimeMs, out var queue) || queue.Count == 0) throw new InvalidDataException($"No RPU for base-layer frame at {packet.TimeMs} ms.");
                rpu = queue.Dequeue();
            }
            copyNals.Add(dovi.ToP81(rpu.Span)); count++;
            var output = new byte[copyNals.Sum(n => n.Length + 4)]; var position = 0;
            foreach (var nal in copyNals) { BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(position), nal.Length); nal.CopyTo(output.AsMemory(position + 4)); position += nal.Length + 4; }
            converted.Add(packet with { Data = output });
        }
        if (count == 0) throw new InvalidDataException("No Profile 7 pictures in fragment.");
        if (rpus.Values.Any(q => q.Count > 0)) throw new InvalidDataException("Unpaired enhancement-layer RPU remains in fragment.");
        var definitions = Tracks.Where(t => t != el).Select(t =>
        {
            if (t != bl) return Ebml.Bytes(0xae, t.Definition);
            var fields = Ebml.Read(t.Definition).Where(e => e.Id != 0x41e4).Select(e =>
            {
                if (e.Id != 0x63a2) return Ebml.Bytes(e.Id, e.Data.ToArray());
                var hvcc = e.Data.ToArray(); hvcc[21] = (byte)(hvcc[21] | 3); return Ebml.Bytes(e.Id, hvcc);
            });
            // dvcC: version 1.0, profile 8, level 6, RPU + BL, compatibility 1.
            var config = new byte[24]; config[0] = 1; config[2] = (byte)(16 | (level >> 5)); config[3] = (byte)((level << 3) | 5); config[4] = 16;
            var mapping = Ebml.Bytes(0x41e4, Ebml.Number(0x41f0, 1), Ebml.Text(0x41a4, "Dolby Vision configuration"),
                Ebml.Number(0x41e7, 0x64766343), Ebml.Bytes(0x41ed, config));
            return Ebml.Bytes(0xae, Ebml.Join(fields), mapping);
        });
        return (Ebml.Bytes(0x1654ae6b, Ebml.Join(definitions)), converted, bl.Number, count);
    }
    internal static IEnumerable<ReadOnlyMemory<byte>> Nals(ReadOnlyMemory<byte> data, int width)
    {
        var cursor = 0;
        while (cursor < data.Length)
        {
            if (cursor + width > data.Length) throw new InvalidDataException("Truncated HEVC length.");
            var size = 0; for (var i = 0; i < width; i++) size = checked(size * 256 + data.Span[cursor++]);
            if (size < 2 || size > data.Length - cursor) throw new InvalidDataException("Invalid HEVC NAL length.");
            yield return data.Slice(cursor, size); cursor += size;
        }
    }
    internal static byte[] Cluster(IEnumerable<Packet> packets, long origin)
    {
        var list = packets.ToArray();
        if (list.Length == 0) throw new InvalidDataException("Empty conversion segment.");
        var time = Math.Max(0, list.Min(p => p.TimeMs) - origin);
        var timestamp = Ebml.Number(0xe7, (ulong)time);
        var lengths = list.Select(p =>
        {
            var block = Ebml.Size((ulong)p.Track).Length + 3 + p.Data.Length; var bytes = 1 + Ebml.Size((ulong)block).Length + block;
            if (p.Duration is { } duration) { bytes += Ebml.Number(0x9b, checked((ulong)duration)).Length; bytes += 1 + Ebml.Size((ulong)bytes).Length; }
            return bytes;
        }).ToArray();
        var payload = checked(timestamp.Length + lengths.Sum()); var size = Ebml.Size((ulong)payload);
        var result = new byte[checked(4 + size.Length + payload)]; BinaryPrimitives.WriteUInt32BigEndian(result, 0x1f43b675); size.CopyTo(result, 4);
        using var body = new MemoryStream(result, 4 + size.Length, payload, writable: true); body.Write(timestamp);
        Span<byte> header = stackalloc byte[3];
        foreach (var p in list)
        {
            var relative = checked((short)(p.TimeMs - origin - time));
            var track = Ebml.Size((ulong)p.Track); var blockSize = track.Length + 3 + p.Data.Length;
            var blockHeader = Ebml.Size((ulong)blockSize); var duration = p.Duration is { } d ? Ebml.Number(0x9b, checked((ulong)d)) : [];
            if (duration.Length > 0) { body.WriteByte(0xa0); body.Write(Ebml.Size((ulong)(1 + blockHeader.Length + blockSize + duration.Length))); }
            body.WriteByte(duration.Length > 0 ? (byte)0xa1 : (byte)0xa3); body.Write(blockHeader); body.Write(track);
            BinaryPrimitives.WriteInt16BigEndian(header, relative); header[2] = (byte)((p.Flags & 0x09) | (p.Key ? 0x80 : 0));
            body.Write(header); body.Write(p.Data.Span); body.Write(duration);
        }
        return result;
    }
}
