using System.Buffers.Binary;

// tsMuxer 2.7.0 authors BL+RPU single-track P8.1 as an enhancement-only subpath,
// leaving no primary video. This finite fixture promotes that existing track;
// the M2TS PID, timestamps and CLPI seek addresses remain unchanged.
internal static class MplsSingleLayer
{
    internal static byte[] Promote(byte[] original, int videoPid)
    {
        var data = original.ToArray();
        int U16(int p) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
        int U32(int p) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)));
        if (!data.AsSpan(0, 4).SequenceEqual("MPLS"u8)) throw new Exception("Invalid MPLS.");
        var playlist = U32(8);
        var end = playlist + 4 + U32(playlist);
        if (U16(playlist + 6) != 1 || U16(playlist + 8) != 1) throw new Exception("Expected one primary item and one obsolete DV subpath.");
        var item = playlist + 10;
        var itemEnd = item + 2 + U16(item);
        var stn = item + 34;
        if (stn + 2 + U16(stn) != itemEnd || data[stn + 4] != 0 || data[stn + 5] != 1 || data[stn + 11] != 1 ||
            data.AsSpan(stn + 6, 5).ContainsAnyExcept((byte)0)) throw new Exception("Unexpected tsMuxer stream table.");
        int EntryEnd(int offset) { var attributes = offset + 1 + data[offset]; return attributes + 1 + data[attributes]; }
        var audio = stn + 16;
        var video = EntryEnd(audio);
        if (EntryEnd(video) != itemEnd || data[video] != 9 || data[video + 1] != 4 ||
            U16(video + 3) != videoPid) throw new Exception("Invalid DV subpath stream entry.");
        var primary = data.AsSpan(video, itemEnd - video).ToArray();
        primary.AsSpan(1, 9).Clear(); primary[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(primary.AsSpan(2), (ushort)videoPid);
        primary.CopyTo(data, audio);
        original.AsSpan(audio, video - audio).CopyTo(data.AsSpan(audio + primary.Length));
        data[stn + 4] = 1; data[stn + 11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(playlist + 8), 0);
        // Remove the no-longer-referenced subpath and relocate following sections.
        var removed = end - itemEnd;
        var result = new byte[data.Length - removed];
        data.AsSpan(0, itemEnd).CopyTo(result);
        data.AsSpan(end).CopyTo(result.AsSpan(itemEnd));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(playlist), (uint)(itemEnd - playlist - 4));
        foreach (var headerOffset in new[] { 12, 16 })
        {
            var old = U32(headerOffset);
            if (old != 0)
            {
                if (old < end) throw new Exception("Unexpected MPLS section order.");
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headerOffset), (uint)(old - removed));
            }
        }
        return result;
    }
}
