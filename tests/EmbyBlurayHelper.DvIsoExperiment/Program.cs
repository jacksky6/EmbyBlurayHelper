// Local finite-sample diagnostic, excluded from all product packages. It uses
// the production UDF writer, independent DiscUtils/BDInfo readers and tsMuxer's
// new navigation. It never modifies Emby settings or original media.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DiscUtils.Udf;
using EmbyBlurayHelper.ProbeWrapper;
using EmbyBlurayHelper.Proxy.Chapters;
using EmbyBlurayHelper.Proxy.DolbyVision;

if (args.Length != 1) throw new ArgumentException("Pass the prepared ISO experiment directory.");
var root = Path.GetFullPath(args[0]);
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "experiment.json")));
var experiment = manifest.RootElement;
if (experiment.GetProperty("status").GetString() != "authored-for-iso-inspection") throw new InvalidOperationException("Prepare the finite source first.");
var authored = experiment.GetProperty("authoredDirectory").GetString()!;
var native = experiment.GetProperty("nativeM2ts").GetString()!;
var generic = experiment.GetProperty("genericM2ts").GetString()!;
var descriptors = Transport.Descriptors(generic);
var patched = Enumerable.Range(0, 100).Select(i => Path.Combine(root, $"p81-described-{i}.m2ts")).First(p => !File.Exists(p));
var patchStats = Transport.Patch(native, patched, descriptors);
if (patchStats.Patched == 0) throw new Exception("No PMT was patched.");
var navigation = MplsSingleLayer.Promote(File.ReadAllBytes(Path.Combine(authored, "BDMV", "PLAYLIST", "00000.mpls")), patchStats.VideoPid);
var reports = new List<object>();
foreach (var (name, transport) in new[] { ("DV ISO Test 04 - P81 Native", native), ("DV ISO Test 05 - P81 Signaled", patched) })
{
    var directory = Path.Combine(root, "media", name);
    Directory.CreateDirectory(directory);
    var target = Path.Combine(directory, name + ".iso");
    var files = Directory.EnumerateFiles(authored, "*", SearchOption.AllDirectories).Select(file =>
    {
        var relative = Path.GetRelativePath(authored, file).Replace('\\', '/');
        if (relative.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase))
            return new VirtualUdfImage.ImageFile(relative, transport, new FileInfo(transport).Length);
        var bytes = relative.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase) ? navigation : File.ReadAllBytes(file);
        return new VirtualUdfImage.ImageFile(relative, null, bytes.Length, bytes);
    }).ToArray();
    var image = VirtualUdfImage.Create(files);
    using (var input = image.Open())
    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
    using var disk = File.OpenRead(target);
    using var udf = new UdfReader(disk);
    using var metadata = IsoDiscMetadata.Open(udf, default);
    var playlist = metadata.SelectPlaylist(default);
    if (playlist.StreamClips.Count != 1 || playlist.Chapters.Count != 3 || Math.Abs(playlist.TotalLength - 60) > 1)
        throw new Exception("Authored ISO playlist or chapters are invalid.");
    using var media = udf.OpenFile(@"\BDMV\STREAM\00000.m2ts", FileMode.Open, FileAccess.Read);
    using var original = File.OpenRead(transport);
    var mediaHash = Convert.ToHexString(SHA256.HashData(media));
    if (mediaHash != Convert.ToHexString(SHA256.HashData(original))) throw new Exception("UDF media content changed.");
    using var clip = udf.OpenFile(@"\BDMV\CLIPINF\00000.clpi", FileMode.Open, FileAccess.Read);
    var clipBytes = new byte[clip.Length]; clip.ReadExactly(clipBytes);
    var index = ClpiIndex.Read(clipBytes, patchStats.VideoPid);
    if (index.Bytes != media.Length || index.Points.Length < 10) throw new Exception("CLPI size or seek index is invalid.");
    // Validate each CLPI byte address against the real transport PES boundary.
    foreach (var point in index.Points)
    {
        media.Position = point.Offset;
        var packet = new byte[192]; media.ReadExactly(packet);
        var pid = ((packet[5] & 31) << 8) | packet[6];
        if (packet[4] != 0x47 || pid != patchStats.VideoPid || (packet[5] & 64) == 0)
            throw new Exception("CLPI points outside a video PES start.");
    }
    reports.Add(new { name, file = target, bytes = image.Length, transport, mediaBytes = media.Length, mediaHash,
        playlist = playlist.Name, durationSeconds = playlist.TotalLength, chapters = playlist.Chapters,
        seekPoints = index.Points.Length, videoPid = patchStats.VideoPid });
    Console.WriteLine($"PASS {name}: {image.Length} bytes; {index.Points.Length} validated seek points; {playlist.TotalLength:F3}s; 3 chapters; UDF member hash identical.");
}
File.WriteAllText(Path.Combine(root, "iso-inspection.json"), JsonSerializer.Serialize(new
{
    createdUtc = DateTimeOffset.UtcNow,
    status = "passed-structure-awaiting-codec-and-device-check",
    udfRevision = VirtualUdfImage.FormatRevision,
    descriptorHex = Convert.ToHexString(descriptors), patchStats,
    navigationFix = "tsMuxer treats any RPU as a secondary DV track; promote the single HEVC BL+RPU track to primary video and remove the obsolete DV subpath in both playlist copies.",
    samples = reports
}, new JsonSerializerOptions { WriteIndented = true }));

internal static class Transport
{
    internal sealed record PatchStats(int Patched, int VideoPid, long Bytes);
    internal static byte[] Descriptors(string file)
    {
        using var input = File.OpenRead(file);
        var packet = new byte[192];
        while (input.Position + packet.Length <= input.Length)
        {
            input.ReadExactly(packet);
            var section = Pmt(packet);
            if (section is null) continue;
            foreach (var entry in Entries(section))
            {
                if (section[entry.Offset] != 0x24) continue;
                var descriptors = new List<byte>();
                for (var p = entry.Offset + 5; p < entry.End; p += 2 + section[p + 1])
                    if (section[p] == 0xb0 || section[p] == 5 && section.AsSpan(p + 2, section[p + 1]).SequenceEqual("DOVI"u8))
                        descriptors.AddRange(section.AsSpan(p, 2 + section[p + 1]).ToArray());
                var result = descriptors.ToArray();
                if (result.Length != 13 || result[6] != 0xb0 || result[10] >> 1 != 8 || (result[11] & 7) != 5 || result[12] >> 4 != 1)
                    throw new Exception("Generic M2TS does not describe P8.1 BL+RPU without EL.");
                return result;
            }
        }
        throw new Exception("Generic transport has no HEVC PMT descriptors.");
    }
    internal static PatchStats Patch(string inputPath, string outputPath, byte[] descriptors)
    {
        using var input = File.OpenRead(inputPath);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        var block = new byte[192 * 4096]; var patches = 0; var videoPid = -1;
        while (input.Position < input.Length)
        {
            var count = (int)Math.Min(block.Length, input.Length - input.Position); input.ReadExactly(block.AsSpan(0, count));
            for (var i = 0; i < count; i += 192)
            {
                var packet = block.AsSpan(i, 192);
                var section = Pmt(packet);
                if (section is null) continue;
                var entry = Entries(section).Single(e => section[e.Offset] == 0x24);
                videoPid = ((section[entry.Offset + 1] & 31) << 8) | section[entry.Offset + 2];
                var length = entry.End - entry.Offset - 5;
                var replacement = new byte[section.Length + descriptors.Length];
                section.AsSpan(0, entry.End).CopyTo(replacement);
                descriptors.CopyTo(replacement, entry.End);
                section.AsSpan(entry.End).CopyTo(replacement.AsSpan(entry.End + descriptors.Length));
                length += descriptors.Length;
                replacement[entry.Offset + 3] = (byte)(0xf0 | length >> 8); replacement[entry.Offset + 4] = (byte)length;
                var sectionLength = replacement.Length - 3;
                replacement[1] = (byte)((replacement[1] & 0xf0) | sectionLength >> 8); replacement[2] = (byte)sectionLength;
                // Bump PMT version while preserving current_next/reserved bits.
                replacement[5] = (byte)((replacement[5] & 0xc1) | ((((replacement[5] >> 1) + 1) & 31) << 1));
                BinaryPrimitives.WriteUInt32BigEndian(replacement.AsSpan(replacement.Length - 4), Crc(replacement.AsSpan(0, replacement.Length - 4)));
                var start = SectionStart(packet);
                if (start + replacement.Length > packet.Length || packet[(start + section.Length)..].ContainsAnyExcept((byte)0xff))
                    throw new Exception("PMT cannot grow inside existing packet stuffing.");
                packet[start..].Fill(0xff); replacement.CopyTo(packet[start..]); patches++;
            }
            output.Write(block, 0, count);
        }
        if (output.Length != input.Length) throw new Exception("Descriptor insertion changed transport offsets.");
        return new(patches, videoPid, output.Length);
    }
    private static int SectionStart(ReadOnlySpan<byte> packet)
    {
        if (packet[4] != 0x47) throw new Exception("Not 192-byte M2TS.");
        if ((packet[5] & 0x40) == 0 || (packet[7] & 0x10) == 0) return -1;
        var payload = 8 + ((packet[7] & 0x20) != 0 ? 1 + packet[8] : 0);
        return payload < packet.Length ? payload + 1 + packet[payload] : -1;
    }
    private static byte[]? Pmt(ReadOnlySpan<byte> packet)
    {
        if ((((packet[5] & 31) << 8) | packet[6]) != 0x100) return null;
        var start = SectionStart(packet);
        if (start < 0 || start + 12 > packet.Length || packet[start] != 2) return null;
        var size = 3 + ((packet[start + 1] & 15) << 8) + packet[start + 2];
        if (start + size > packet.Length) throw new Exception("Diagnostic expects a single-packet PMT.");
        var section = packet.Slice(start, size).ToArray();
        if (Crc(section) != 0) throw new Exception("Invalid original PMT CRC.");
        return section;
    }
    private static IEnumerable<(int Offset, int End)> Entries(byte[] section)
    {
        var offset = 12 + ((section[10] & 15) << 8) + section[11];
        while (offset < section.Length - 4)
        {
            var end = offset + 5 + ((section[offset + 3] & 15) << 8) + section[offset + 4];
            if (end > section.Length - 4) throw new Exception("Invalid PMT stream entries.");
            yield return (offset, end); offset = end;
        }
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= (uint)value << 24;
            for (var i = 0; i < 8; i++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
        }
        return crc;
    }
}
