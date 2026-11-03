using System.Buffers.Binary;
using System.Text;
using EmbyBlurayHelper.Proxy;

var languages = new Dictionary<ushort, string> { [0x1100] = "eng", [0x12a0] = "zho" };
var count = 0;
foreach (var packetSize in new[] { 188, 192 })
{
    var original = Pack(Section(), packetSize);
    var patched = original.ToArray();
    var stats = TransportLanguagePatcher.PatchBlock(patched, languages);
    Check(stats.Patched == 1 && stats.NoSpace == 0, $"{packetSize}: patch count");
    var pmt = Extract(patched, packetSize);
    Check(Crc(pmt) == 0, $"{packetSize}: valid MPEG-2 section CRC");
    Check(Encoding.ASCII.GetString(pmt).Contains("eng"), $"{packetSize}: audio language embedded");
    Check(Encoding.ASCII.GetString(pmt).Contains("zho"), $"{packetSize}: PGS language embedded");
    Check(patched.AsSpan(packetSize).SequenceEqual(original.AsSpan(packetSize)), $"{packetSize}: media bytes untouched");
    var again = patched.ToArray();
    TransportLanguagePatcher.PatchBlock(again, languages);
    Check(again.SequenceEqual(patched), $"{packetSize}: idempotent output");

    var knownLanguage = Pack(Section("fra"), packetSize);
    TransportLanguagePatcher.PatchBlock(knownLanguage, languages);
    Check(Encoding.ASCII.GetString(Extract(knownLanguage, packetSize)).Contains("fra"), $"{packetSize}: existing language retained");

    var unknownLanguage = Pack(Section("und"), packetSize);
    TransportLanguagePatcher.PatchBlock(unknownLanguage, languages);
    Check(!Encoding.ASCII.GetString(Extract(unknownLanguage, packetSize)).Contains("und"), $"{packetSize}: unknown language replaced");

    var multiPacket = Pack(Section(padding: 190), packetSize);
    var multiStats = TransportLanguagePatcher.PatchBlock(multiPacket, languages);
    Check(multiStats.Patched == 1 && Crc(Extract(multiPacket, packetSize)) == 0, $"{packetSize}: multi-packet section");

    var noSpace = Pack(Section(padding: 150), packetSize);
    var noSpaceCopy = noSpace.ToArray();
    var noSpaceStats = TransportLanguagePatcher.PatchBlock(noSpace, languages);
    Check(noSpaceStats.NoSpace == 1 && noSpace.SequenceEqual(noSpaceCopy), $"{packetSize}: insufficient stuffing forwarded intact");

    var repeated = Pack(Section(padding: 150), packetSize).Concat(Pack(Section(padding: 150), packetSize)).ToArray();
    var secondStart = repeated.Length / 2 + (packetSize == 192 ? 4 : 0);
    var firstPackets = (int)Math.Ceiling((Section(padding: 150).Length + 1) / 184d);
    for (var at = secondStart; at + 188 <= repeated.Length; at += packetSize)
        if ((((repeated[at + 1] & 31) << 8) | repeated[at + 2]) == 0x100)
            repeated[at + 3] = (byte)((repeated[at + 3] & 0xf0) | ((repeated[at + 3] + firstPackets) & 15));
    var repeatedOriginal = repeated.ToArray();
    var changedRepeat = repeated.ToArray();
    var secondSection = secondStart + 5;
    changedRepeat[secondSection + 5] ^= 2;
    BinaryPrimitives.WriteUInt32BigEndian(changedRepeat.AsSpan(secondSection + Section(padding: 150).Length - 4), Crc(changedRepeat.AsSpan(secondSection, Section(padding: 150).Length - 4)));
    var changedOriginal = changedRepeat.ToArray();
    var changedStats = TransportLanguagePatcher.PatchBlock(changedRepeat, languages);
    Check(changedStats.Patched == 0 && changedRepeat.SequenceEqual(changedOriginal), $"{packetSize}: different PMT versions are never combined");
    var repeatedStats = TransportLanguagePatcher.PatchBlock(repeated, languages);
    Check(repeatedStats.Patched == 1 && Crc(Extract(repeated, packetSize)) == 0, $"{packetSize}: crowded PMT safely spans an identical repetition ({repeatedStats}, CRC {Crc(Extract(repeated, packetSize)):x}, section {Section(padding: 150).Length})");
    Check(Encoding.ASCII.GetString(Extract(repeated, packetSize)).Contains("eng") && Encoding.ASCII.GetString(Extract(repeated, packetSize)).Contains("zho"), $"{packetSize}: crowded PMT retains all language descriptors");
    var patchedRepeat = repeated.ToArray();
    TransportLanguagePatcher.PatchBlock(repeated, languages);
    Check(repeated.SequenceEqual(patchedRepeat), $"{packetSize}: extended PMT output is idempotent");
    for (var at = packetSize == 192 ? 4 : 0; at + 188 <= repeated.Length; at += packetSize)
        Check(repeated[at + 3] == repeatedOriginal[at + 3] &&
            ((((repeated[at + 1] & 31) << 8) | repeated[at + 2]) == 0x100 || repeated.AsSpan(at, 188).SequenceEqual(repeatedOriginal.AsSpan(at, 188))),
            $"{packetSize}: repeated PMT preserves continuity and other PID payloads");

    var badCrc = original.ToArray();
    badCrc[packetSize == 192 ? 28 : 24] ^= 1;
    var badCrcCopy = badCrc.ToArray();
    TransportLanguagePatcher.PatchBlock(badCrc, languages);
    Check(badCrc.SequenceEqual(badCrcCopy), $"{packetSize}: corrupt section forwarded intact");

    var nonStuffing = original.ToArray();
    nonStuffing[packetSize - 1] = 0;
    var nonStuffingCopy = nonStuffing.ToArray();
    TransportLanguagePatcher.PatchBlock(nonStuffing, languages);
    Check(nonStuffing.SequenceEqual(nonStuffingCopy), $"{packetSize}: other PSI payload retained");
}

// The same absolute byte must have the same value for full, overlapping,
// unaligned, and retried Range requests, independent of network read sizes.
var blockSize = TransportLanguagePatcher.BlockSize;
var full = new byte[blockSize * 2];
var fixture = Pack(Section(), 192);
for (var offset = 0; offset + fixture.Length <= full.Length; offset += fixture.Length)
    fixture.CopyTo(full, offset);
// Complete trailing packets with filler.
for (var offset = 0; offset < full.Length; offset += 192)
    if (full[offset + 4] == 0) { full[offset + 4] = 0x47; full[offset + 5] = 0x1f; full[offset + 6] = 0xff; full[offset + 7] = 0x10; }
using var fullOutput = new MemoryStream();
await TransportLanguagePatcher.CopyAsync(new MemoryStream(full), fullOutput, 0, 0, full.Length, languages, default);
var expected = fullOutput.ToArray();
foreach (var (start, length) in new[] { (123, 3256), (blockSize - 137, 485), (blockSize + 47, 1024) })
{
    var upstreamStart = start / blockSize * blockSize;
    using var rangeOutput = new MemoryStream();
    await TransportLanguagePatcher.CopyAsync(new ShortReads(full[upstreamStart..]), rangeOutput,
        upstreamStart, start, length, languages, default);
    Check(rangeOutput.ToArray().AsSpan().SequenceEqual(expected.AsSpan(start, length)), $"Range {start}+{length}: deterministic bytes");
}
var random = new Random(2406);
for (var test = 0; test < 100; test++)
{
    var invalid = new byte[random.Next(0, 2048)];
    random.NextBytes(invalid);
    var copy = invalid.ToArray();
    TransportLanguagePatcher.PatchBlock(invalid, languages);
    Check(invalid.SequenceEqual(copy), "Unrecognized data forwarded intact");
}
count += await WholeClipChecks.RunAsync();
Console.WriteLine($"Passed {count} transport and playlist checks.");

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    count++;
}

static byte[] Section(string? audioLanguage = null, int padding = 0)
{
    using var stream = new MemoryStream();
    stream.Write([2, 0xb0, 0, 0, 1, 0xc1, 0, 0, 0xf0, 0x11, 0xf0, 0]);
    var descriptors = new List<byte>();
    if (audioLanguage is not null) descriptors.AddRange([0x0a, 4, .. Encoding.ASCII.GetBytes(audioLanguage), 0]);
    if (padding > 0) descriptors.AddRange([0x52, (byte)(padding - 2), .. new byte[padding - 2]]);
    stream.Write([0x86, 0xf1, 0, (byte)(0xf0 | (descriptors.Count >> 8)), (byte)descriptors.Count]);
    stream.Write(descriptors.ToArray());
    stream.Write([0x90, 0xf2, 0xa0, 0xf0, 0]);
    stream.Write(new byte[4]);
    var bytes = stream.ToArray();
    bytes[1] = (byte)(0xb0 | ((bytes.Length - 3) >> 8));
    bytes[2] = (byte)(bytes.Length - 3);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), Crc(bytes.AsSpan(0, bytes.Length - 4)));
    return bytes;
}

static byte[] Pack(byte[] section, int packetSize)
{
    var packets = (int)Math.Ceiling((section.Length + 1) / 184d);
    var data = Enumerable.Repeat((byte)0xff, (packets + 2) * packetSize).ToArray();
    var copied = 0;
    var sync = packetSize == 192 ? 4 : 0;
    for (var index = 0; index < packets + 2; index++)
    {
        var offset = index * packetSize + sync;
        data[offset] = 0x47;
        data[offset + 1] = (byte)(index == 0 ? 0x41 : index < packets ? 1 : 0x11);
        data[offset + 2] = 0;
        data[offset + 3] = (byte)(0x10 | (index & 0xf));
        if (index >= packets) continue;
        var payload = offset + 4;
        if (index == 0) data[payload++] = 0;
        var length = Math.Min(section.Length - copied, offset + 188 - payload);
        section.AsSpan(copied, length).CopyTo(data.AsSpan(payload));
        copied += length;
    }
    return data;
}

static byte[] Extract(byte[] packets, int packetSize)
{
    var sync = packetSize == 192 ? 4 : 0;
    var length = 3 + ((packets[sync + 6] & 0xf) << 8) + packets[sync + 7];
    var bytes = new byte[length];
    var copied = 0;
    for (var packet = sync; copied < length; packet += packetSize)
    {
        if ((((packets[packet + 1] & 31) << 8) | packets[packet + 2]) != 0x100) continue;
        var payload = packet + 4;
        if (((packets[packet + 3] >> 4) & 3) == 3) payload += 1 + packets[packet + 4];
        if ((packets[packet + 1] & 0x40) != 0) payload += 1 + packets[payload];
        var count = Math.Min(length - copied, packet + 188 - payload);
        packets.AsSpan(payload, count).CopyTo(bytes.AsSpan(copied));
        copied += count;
    }
    return bytes;
}

static uint Crc(ReadOnlySpan<byte> bytes)
{
    uint crc = 0xffffffff;
    foreach (var value in bytes)
        for (var bit = 7; bit >= 0; bit--)
            crc = (crc << 1) ^ ((((crc >> 31) ^ ((uint)value >> bit)) & 1) != 0 ? 0x04c11db7u : 0);
    return crc;
}

sealed class ShortReads(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(17, buffer.Length)], cancellationToken);
}
