using System.Text;

namespace EmbyBlurayHelper.Proxy;

// All ranges use identical absolute block boundaries. Repeated/overlapping range
// requests therefore receive exactly the same bytes, including PMTs near an edge.
internal static class TransportLanguagePatcher
{
    public const int BlockSize = 192 * 188 * 32;

    public static async Task<(int Patched, int NoSpace)> CopyAsync(Stream input, Stream output,
        long upstreamStart, long visibleStart, long visibleLength,
        IReadOnlyDictionary<ushort, string> languages, CancellationToken cancellationToken)
    {
        var buffer = new byte[BlockSize];
        var patched = 0;
        var noSpace = 0;
        var position = upstreamStart;
        var visibleEnd = visibleStart + visibleLength;
        while (position < visibleEnd)
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            if (count == 0) throw new EndOfStreamException("Emby returned a truncated media range.");
            var stats = PatchBlock(buffer.AsSpan(0, count), languages);
            patched += stats.Patched;
            noSpace += stats.NoSpace;
            var from = (int)Math.Max(0, visibleStart - position);
            var to = (int)Math.Min(count, visibleEnd - position);
            if (to > from) await output.WriteAsync(buffer.AsMemory(from, to - from), cancellationToken);
            position += count;
        }
        return (patched, noSpace);
    }

    public static (int Patched, int NoSpace) PatchBlock(Span<byte> block, IReadOnlyDictionary<ushort, string> languages)
    {
        var (packetSize, syncOffset) = Detect(block);
        if (packetSize == 0) return (0, 0);
        var patched = 0;
        var noSpace = 0;
        for (var packet = syncOffset; packet + 188 <= block.Length; packet += packetSize)
        {
            var payload = Payload(block, packet);
            if (payload < 0 || (block[packet + 1] & 0x40) == 0) continue;
            var sectionStart = payload + 1 + block[payload];
            if (sectionStart + 12 > packet + 188 || block[sectionStart] != 2) continue;
            var length = 3 + ((block[sectionStart + 1] & 0x0f) << 8) + block[sectionStart + 2];
            if (length is < 16 or > 1024) continue;
            var pid = Pid(block, packet);
            var parts = new List<(int Start, int End)> { (sectionStart, packet + 188) };
            var capacity = packet + 188 - sectionStart;
            var lastPacket = packet;
            var continuity = block[packet + 3] & 0xf;
            for (var next = packet + packetSize; capacity < length && next + 188 <= block.Length; next += packetSize)
            {
                if (Pid(block, next) != pid) continue;
                var nextPayload = Payload(block, next);
                if (nextPayload < 0) continue;
                if ((block[next + 1] & 0x40) != 0 || (block[next + 3] & 0xf) != ((continuity + 1) & 0xf)) break;
                continuity = block[next + 3] & 0xf;
                parts.Add((nextPayload, next + 188));
                capacity += next + 188 - nextPayload;
                lastPacket = next;
            }
            if (capacity < length) continue;
            var original = new byte[capacity];
            var copied = 0;
            foreach (var part in parts)
            {
                block[part.Start..part.End].CopyTo(original.AsSpan(copied));
                copied += part.End - part.Start;
            }
            // Do not consume another PSI section or any non-stuffing payload.
            if (Crc(original.AsSpan(0, length)) != 0 || original.AsSpan(length).ContainsAnyExcept((byte)0xff)) continue;
            var replacement = AddLanguages(original.AsSpan(0, length), languages);
            if (replacement is null) continue;
            if (replacement.Length > capacity && !ExtendIntoRepeat(block, packetSize, pid, original.AsSpan(0, length),
                replacement.Length, parts, ref capacity, ref lastPacket, continuity)) { noSpace++; continue; }
            var offset = 0;
            foreach (var part in parts)
            {
                block[part.Start..part.End].Fill(0xff);
                var bytes = Math.Min(replacement.Length - offset, part.End - part.Start);
                if (bytes > 0) replacement.AsSpan(offset, bytes).CopyTo(block[part.Start..part.End]);
                offset += bytes;
            }
            patched++;
            packet = lastPacket;
        }
        return (patched, noSpace);
    }

    private static bool ExtendIntoRepeat(Span<byte> block, int packetSize, ushort pid, ReadOnlySpan<byte> original,
        int required, List<(int Start, int End)> parts, ref int capacity, ref int lastPacket, int continuity)
    {
        // A crowded PMT can need more bytes than its current stuffing allows.
        // Reuse the next *identical* PMT repetition's packets as continuation.
        // Keep packet count, timestamps, media payloads and continuity counters;
        // only the redundant section's PUSI/pointer and PSI bytes change.
        var extra = new List<(int Start, int End)>();
        var repeat = new List<byte>();
        var repeatStart = -1;
        var endPacket = lastPacket;
        for (var packet = lastPacket + packetSize; packet + 188 <= block.Length; packet += packetSize)
        {
            if (Pid(block, packet) != pid) continue;
            var payload = Payload(block, packet);
            if (payload < 0) continue;
            if ((block[packet + 3] & 0xf) != ((continuity + 1) & 0xf)) return false;
            continuity = block[packet + 3] & 0xf;
            var begin = payload;
            if (repeatStart < 0)
            {
                if ((block[packet + 1] & 0x40) == 0 || block[payload] != 0) return false;
                repeatStart = packet;
                begin++;
            }
            else if ((block[packet + 1] & 0x40) != 0) return false;
            extra.Add((payload, packet + 188));
            repeat.AddRange(block[begin..(packet + 188)].ToArray());
            endPacket = packet;
            if (repeat.Count >= original.Length) break;
        }
        if (repeat.Count < original.Length || !original.SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(repeat)[..original.Length]) ||
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(repeat)[original.Length..].ContainsAnyExcept((byte)0xff) ||
            capacity + extra.Sum(p => p.End - p.Start) < required) return false;
        block[repeatStart + 1] &= 0xbf;
        parts.AddRange(extra);
        capacity += extra.Sum(p => p.End - p.Start);
        lastPacket = endPacket;
        return true;
    }

    private static byte[]? AddLanguages(ReadOnlySpan<byte> section, IReadOnlyDictionary<ushort, string> languages)
    {
        var programInfoLength = ((section[10] & 0xf) << 8) + section[11];
        var streamsStart = 12 + programInfoLength;
        if (streamsStart > section.Length - 4) return null;
        using var output = new MemoryStream();
        output.Write(section[..streamsStart]);
        var changed = false;
        for (var offset = streamsStart; offset < section.Length - 4;)
        {
            if (offset + 5 > section.Length - 4) return null;
            var infoLength = ((section[offset + 3] & 0xf) << 8) + section[offset + 4];
            var end = offset + 5 + infoLength;
            if (end > section.Length - 4) return null;
            var pid = (ushort)(((section[offset + 1] & 0x1f) << 8) + section[offset + 2]);
            var entry = section[offset..end].ToArray();
            var languageOffset = -1;
            for (var descriptor = 5; descriptor < entry.Length;)
            {
                if (descriptor + 2 > entry.Length || descriptor + 2 + entry[descriptor + 1] > entry.Length) return null;
                if (entry[descriptor] == 0x0a && entry[descriptor + 1] >= 4) languageOffset = descriptor + 2;
                descriptor += 2 + entry[descriptor + 1];
            }
            if (languages.TryGetValue(pid, out var language) && language.Length == 3 && language.All(char.IsAsciiLetter))
            {
                if (languageOffset >= 0)
                {
                    var existing = Encoding.ASCII.GetString(entry, languageOffset, 3);
                    if (existing.Equals("und", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(existing))
                    {
                        Encoding.ASCII.GetBytes(language, entry.AsSpan(languageOffset, 3));
                        changed = true;
                    }
                    output.Write(entry);
                }
                else
                {
                    var newLength = infoLength + 6;
                    entry[3] = (byte)((entry[3] & 0xf0) | (newLength >> 8));
                    entry[4] = (byte)newLength;
                    output.Write(entry);
                    output.Write([0x0a, 4, (byte)language[0], (byte)language[1], (byte)language[2], 0]);
                    changed = true;
                }
            }
            else output.Write(entry);
            offset = end;
        }
        if (!changed || output.Length + 4 > 1024) return null;
        output.Write(new byte[4]);
        var result = output.ToArray();
        result[1] = (byte)((result[1] & 0xf0) | ((result.Length - 3) >> 8));
        result[2] = (byte)(result.Length - 3);
        var crc = Crc(result.AsSpan(0, result.Length - 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), crc);
        return result;
    }

    private static (int Size, int Offset) Detect(ReadOnlySpan<byte> block)
    {
        foreach (var (size, offset) in new[] { (192, 4), (188, 0) })
            if (block.Length > offset + size * 2 && block[offset] == 0x47 &&
                block[offset + size] == 0x47 && block[offset + size * 2] == 0x47) return (size, offset);
        return (0, 0);
    }

    private static int Payload(ReadOnlySpan<byte> block, int packet)
    {
        if (block[packet] != 0x47 || (block[packet + 1] & 0x80) != 0 || (block[packet + 3] & 0xc0) != 0) return -1;
        var control = (block[packet + 3] >> 4) & 3;
        if ((control & 1) == 0) return -1;
        var start = packet + 4 + (control == 3 ? 1 + block[packet + 4] : 0);
        return start < packet + 188 ? start : -1;
    }

    private static ushort Pid(ReadOnlySpan<byte> block, int packet) => (ushort)(((block[packet + 1] & 0x1f) << 8) + block[packet + 2]);

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
        }
        return crc;
    }
}
