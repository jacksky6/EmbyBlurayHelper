using System.Buffers.Binary;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

// CLPI CPI entry points, not bitrate estimates. Layout follows Blu-ray's
// coarse/fine EP map; PTS values here use 45 kHz, including the quantized map.
internal sealed record ClpiIndex(long Start45, long End45, long Bytes, ClpiIndex.Point[] Points)
{
    internal sealed record Point(long Time45, long Offset);
    internal static ClpiIndex Read(byte[] data, int pid = 0x1011)
    {
        if (data.Length is < 64 or > 4 * 1024 * 1024 || !data.AsSpan(0, 4).SequenceEqual("HDMV"u8)) throw new InvalidDataException("Invalid CLPI.");
        uint U32(int p) => p >= 0 && p <= data.Length - 4 ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)) : throw new InvalidDataException("Truncated CLPI.");
        var sequence = checked((int)U32(8));
        if (sequence + 26 > data.Length || data[sequence + 5] != 1 || data[sequence + 10] != 1 || U32(sequence + 6) != 0 || U32(sequence + 14) != 0)
            throw new InvalidDataException("Multiple Blu-ray clock domains are unsupported for DV conversion.");
        var start = U32(sequence + 18); var stop = U32(sequence + 22); var bytes = U32(56) * 192L;
        var cpi = checked((int)U32(16)); var end = checked(cpi + 4 + (int)U32(cpi));
        if (end > data.Length || cpi + 8 > end || stop <= start || bytes <= 0) throw new InvalidDataException("Invalid CLPI CPI section.");
        var bits = new Bits(data, (cpi + 4) * 8); bits.Get(12); var type = bits.Get(4); var mapOrigin = cpi + 6; bits.Get(8); var count = bits.Get(8);
        if (type != 1 || count is < 1 or > 32) throw new InvalidDataException("Missing CLPI random-access map.");
        var maps = new List<(int Pid, int Coarse, int Fine, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            var id = bits.Get(16); bits.Get(10); bits.Get(4); var coarse = bits.Get(16); var fine = bits.Get(18); var offset = bits.Get(32);
            maps.Add((checked((int)id), checked((int)coarse), checked((int)fine), checked(mapOrigin + (int)offset)));
        }
        var map = maps.SingleOrDefault(m => m.Pid == pid);
        if (map.Coarse is < 1 or > 65535 || map.Fine is < 1 or > 262143 || map.Offset < cpi || map.Offset + 4L + map.Coarse * 8L > end)
            throw new InvalidDataException("Invalid base-layer CLPI index.");
        var fineStart = checked(map.Offset + (int)U32(map.Offset));
        if (fineStart < map.Offset || fineStart + 4L * map.Fine > end) throw new InvalidDataException("Invalid CLPI fine index.");
        var coarseEntries = new List<(int Ref, long Pts, long Spn)>(); bits.Position = (map.Offset + 4) * 8;
        for (var i = 0; i < map.Coarse; i++) coarseEntries.Add((checked((int)bits.Get(18)), bits.Get(14), bits.Get(32)));
        if (coarseEntries[0].Ref != 0 || coarseEntries.Any(c => c.Ref >= map.Fine) || !coarseEntries.Select(c => c.Ref).SequenceEqual(coarseEntries.Select(c => c.Ref).Distinct().Order()))
            throw new InvalidDataException("Invalid CLPI coarse references.");
        var points = new List<Point>(); var coarseIndex = 0; bits.Position = fineStart * 8;
        for (var i = 0; i < map.Fine; i++)
        {
            while (coarseIndex + 1 < coarseEntries.Count && i >= coarseEntries[coarseIndex + 1].Ref) coarseIndex++;
            var coarse = coarseEntries[coarseIndex]; bits.Get(4); var pts = bits.Get(11); var spn = bits.Get(17);
            var time = ((coarse.Pts & ~1L) << 18) + (pts << 8); var offset = ((coarse.Spn & ~0x1ffffL) + spn) * 192;
            if (offset >= bytes || points.Count > 0 && (offset <= points[^1].Offset || time <= points[^1].Time45)) throw new InvalidDataException("Non-monotonic CLPI entry points.");
            points.Add(new(time, offset));
        }
        return new(start, stop, bytes, points.ToArray());
    }
    private sealed class Bits(byte[] data, int position)
    {
        internal int Position = position;
        internal long Get(int count)
        {
            if (Position < 0 || Position + (long)count > data.Length * 8L) throw new InvalidDataException("Truncated CLPI bitfield.");
            long n = 0; for (var i = 0; i < count; i++, Position++) n = (n << 1) | (uint)((data[Position / 8] >> (7 - Position % 8)) & 1); return n;
        }
    }
}
