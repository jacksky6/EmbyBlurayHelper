namespace EmbyBlurayHelper.Proxy.DolbyVision;

// Seekable virtual MKV: small fixed header + indexed bounded cluster slots.
// Padding is an EBML Void, not a disk allocation or a second converted movie.
internal static class VirtualMatroska
{
    internal const int HeaderBytes = 1024 * 1024;
    internal static byte[] Header(DvDiscPlan plan, DvFragment first)
    {
        var ebml = Ebml.Bytes(0x1a45dfa3, Ebml.Number(0x4286, 1), Ebml.Number(0x42f7, 1), Ebml.Number(0x42f2, 4), Ebml.Number(0x42f3, 8),
            Ebml.Text(0x4282, "matroska"), Ebml.Number(0x4287, 4), Ebml.Number(0x4285, 2));
        var segmentStart = ebml.Length + 12;
        var info = Ebml.Bytes(0x1549a966, Ebml.Number(0x2ad7b1, 1000000), Ebml.Text(0x4d80, "EmbyBlurayHelper"),
            Ebml.Text(0x5741, "EmbyBlurayHelper P8.1"), Ebml.Float(0x4489, plan.DurationMs));
        var chapters = Ebml.Bytes(0x1043a770, Ebml.Bytes(0x45b9, Ebml.Join(plan.Chapters.Select((t, i) => Ebml.Bytes(0xb6,
            Ebml.Number(0x73c4, (ulong)i + 1), Ebml.Number(0x91, (ulong)t * 1000000), Ebml.Number(0x98, 0), Ebml.Number(0x4598, 1),
            Ebml.Bytes(0x80, Ebml.Text(0x85, "Chapter " + (i + 1)), Ebml.Text(0x437c, "eng")))))));
        var cues = Ebml.Bytes(0x1c53bb6b, Ebml.Join(plan.Segments.Select(s => Ebml.Bytes(0xbb, Ebml.Number(0xb3, (ulong)s.ApproxStartMs),
            Ebml.Bytes(0xb7, Ebml.Number(0xf7, (ulong)first.VideoTrack), Ebml.Number(0xf1, (ulong)(HeaderBytes - segmentStart + s.SlotOffset), 8))))));
        byte[] Seek(uint id, long pos)
        {
            var code = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(code, id);
            return Ebml.Bytes(0x4dbb, Ebml.Bytes(0x53ab, code), Ebml.Number(0x53ac, (ulong)pos, 8));
        }
        byte[] Index(long start) => Ebml.Bytes(0x114d9b74, Seek(0x1549a966, start), Seek(0x1654ae6b, start + info.Length),
            Seek(0x1043a770, start + info.Length + first.Tracks.Length), Seek(0x1c53bb6b, start + info.Length + first.Tracks.Length + chapters.Length));
        var seek = Index(Index(0).Length); var content = Ebml.Join([seek, info, first.Tracks, chapters, cues]);
        var padding = HeaderBytes - segmentStart - content.Length;
        if (padding < 9) throw new InvalidDataException("DV navigation exceeds header budget.");
        return Ebml.Join([ebml, new byte[] { 0x18, 0x53, 0x80, 0x67 }, Ebml.Size((ulong)(plan.Length - segmentStart), 8), content, Void(padding)]);
    }
    internal static byte[] Void(int length)
    {
        if (length < 9) throw new InvalidDataException("EBML padding too small.");
        var result = new byte[length]; result[0] = 0xec; Ebml.Size((ulong)(length - 9), 8).CopyTo(result, 1); return result;
    }
    internal static byte[] Slot(DvDiscPlan.Segment segment, DvFragment fragment)
    {
        if (segment.SlotBytes - fragment.Cluster.Length < 9) throw new InvalidDataException("DV slot overflow.");
        var slot = new byte[segment.SlotBytes]; fragment.Cluster.CopyTo(slot, 0);
        slot[fragment.Cluster.Length] = 0xec; Ebml.Size((ulong)(slot.Length - fragment.Cluster.Length - 9), 8).CopyTo(slot, fragment.Cluster.Length + 1);
        return slot;
    }
}
