using System.Buffers.Binary;
using System.Text;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

// Small, bounded Matroska reader/writer. Media payloads are slices of the
// fragment buffer; parsing never recursively copies the video.
internal static class Ebml
{
    internal readonly record struct Element(uint Id, ReadOnlyMemory<byte> Data);
    internal static IEnumerable<Element> Read(ReadOnlyMemory<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var id = Vint(data.Span, ref offset, true);
            var length = Vint(data.Span, ref offset, false);
            if (length == ulong.MaxValue) length = (ulong)(data.Length - offset);
            if (length > (ulong)(data.Length - offset)) throw new InvalidDataException("Truncated EBML element.");
            yield return new(checked((uint)id), data.Slice(offset, (int)length));
            offset += (int)length;
        }
    }
    internal static ulong Vint(ReadOnlySpan<byte> data, ref int offset, bool id = false)
    {
        if (offset >= data.Length || data[offset] == 0) throw new InvalidDataException("Invalid EBML integer.");
        var width = 1; var mask = 128;
        while ((data[offset] & mask) == 0) { width++; mask >>= 1; }
        if (width > (id ? 4 : 8) || offset + width > data.Length) throw new InvalidDataException("Invalid EBML width.");
        ulong value = (ulong)(data[offset++] & (id ? 255 : mask - 1));
        for (var n = 1; n < width; n++) value = (value << 8) | data[offset++];
        return !id && value == (1UL << (7 * width)) - 1 ? ulong.MaxValue : value;
    }
    internal static ulong UInt(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 8) throw new InvalidDataException("Oversized EBML integer.");
        ulong n = 0; foreach (var b in data.Span) n = (n << 8) | b; return n;
    }
    internal static byte[] Size(ulong size, int width = 0)
    {
        if (width == 0) { width = 1; while (width < 8 && size >= (1UL << (width * 7)) - 1) width++; }
        if (width is < 1 or > 8 || size >= (1UL << (width * 7)) - 1) throw new InvalidDataException("EBML size overflow.");
        var data = new byte[width]; var value = size | (1UL << (width * 7));
        for (var n = width - 1; n >= 0; n--) { data[n] = (byte)value; value >>= 8; }
        return data;
    }
    internal static byte[] Bytes(uint id, params byte[][] parts)
    {
        var count = parts.Sum(p => p.Length); var size = Size((ulong)count);
        var width = id > 0xffffff ? 4 : id > 0xffff ? 3 : id > 255 ? 2 : 1;
        var result = new byte[checked(width + size.Length + count)];
        for (var i = 0; i < width; i++) result[width - i - 1] = (byte)(id >> (8 * i));
        size.CopyTo(result, width); var offset = width + size.Length;
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }
    internal static byte[] Number(uint id, ulong n, int width = 0)
    {
        if (width == 0) { width = 1; while (width < 8 && n >= (1UL << (width * 8))) width++; }
        var data = new byte[width]; for (var i = width - 1; i >= 0; i--) { data[i] = (byte)n; n >>= 8; }
        return Bytes(id, data);
    }
    internal static byte[] Text(uint id, string value) => Bytes(id, Encoding.UTF8.GetBytes(value));
    internal static byte[] Float(uint id, double n)
    { var data = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(data, BitConverter.DoubleToInt64Bits(n)); return Bytes(id, data); }
    internal static byte[] Join(IEnumerable<byte[]> parts)
    { using var stream = new MemoryStream(); foreach (var p in parts) stream.Write(p); return stream.ToArray(); }
}
