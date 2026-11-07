using EmbyBlurayHelper.Proxy.DolbyVision;
using System.Security.Cryptography;
using System.Text.Json;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
await PlaybackReportChecks.Run(Check);
await CoreChecks.Run(Check);
foreach (var n in new ulong[] { 0, 1, 126, 127, 128, 16383, 100000000000 })
{ var bytes = Ebml.Size(n); var pos = 0; Check(Ebml.Vint(bytes, ref pos) == n && pos == bytes.Length, "EBML size " + n); }
if (args.Length == 1)
{
    var index = ClpiIndex.Read(File.ReadAllBytes(args[0]));
    Console.WriteLine($"CLPI {index.Start45 / 45000d:F3}-{index.End45 / 45000d:F3}s, {index.Bytes} bytes; {index.Points.Length} access points.");
    foreach (var p in index.Points.Take(6)) Console.WriteLine($"{p.Time45 / 45000d:F6}s, offset {p.Offset}");
}
if (args.Length >= 6 && args[0] == "--http") await IntegrationChecks.Run(args, Check);
else if (args.Length >= 7 && args[0] == "--disc")
{
    using var settings = JsonDocument.Parse(File.ReadAllText(args[1]));
    var url = settings.RootElement.GetProperty("embyUrl").GetString()!;
    if (!new Uri(url).IsLoopback) throw new Exception("Local test only.");
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("X-Emby-Token", settings.RootElement.GetProperty("embyApiKey").GetString());
    using var item = JsonDocument.Parse(await client.GetStringAsync(url.TrimEnd('/') + "/emby/Items?Ids=" + Uri.EscapeDataString(args[2]) + "&Fields=MediaSources,Chapters"));
    var source = item.RootElement.GetProperty("Items")[0].GetProperty("MediaSources")[0];
    Check(DvDiscPlan.Candidate(source), "actual Emby source eligible for P7 conversion");
    var plan = DvDiscPlan.Create(source, CancellationToken.None);
    Console.WriteLine($"Plan {plan.Playlist}: {plan.DurationMs} ms, {plan.Segments.Count} segments, {plan.Length} virtual bytes, {plan.Languages.Count} track languages.");
    Directory.CreateDirectory(args[5]);
    var builder = new DvFragmentBuilder(args[3], args[4]);
    foreach (var index in args[6..].Select(int.Parse))
    {
        var segment = plan.Segments[index]; var result = await builder.Build(plan, segment, CancellationToken.None);
        var simple = Ebml.Join([Ebml.Bytes(0x1a45dfa3, Ebml.Number(0x4286, 1), Ebml.Number(0x42f7, 1), Ebml.Number(0x42f2, 4), Ebml.Number(0x42f3, 8), Ebml.Text(0x4282, "matroska"), Ebml.Number(0x4287, 4), Ebml.Number(0x4285, 2)),
            Ebml.Bytes(0x18538067, Ebml.Bytes(0x1549a966, Ebml.Number(0x2ad7b1, 1000000), Ebml.Text(0x4d80, "Helper"), Ebml.Text(0x5741, "Helper")), result.Tracks, result.Cluster)]);
        File.WriteAllBytes(System.IO.Path.Combine(args[5], $"segment-{index}.mkv"), simple);
        Console.WriteLine($"Segment {index}: {segment.ApproxStartMs}ms, {result.Pictures} frames, {result.Cluster.Length} bytes, input {result.InputBytes} bytes, {result.ElapsedMs}ms.");
    }
}
else if (args.Length >= 3)
{
    using var dovi = new DoviNative(args[0]);
    var bytes = File.ReadAllBytes(args[1]); var fragment = MatroskaFragment.Parse(bytes);
    Console.WriteLine(string.Join("\n", fragment.Tracks.Select(t => $"Track {t.Number}: {t.Type}, {t.Codec}, width {t.Width}")));
    Console.WriteLine(string.Join("\n", fragment.Packets.Where(p => p.Track <= 2).Take(16).Select(p => $"Packet {p.Track}: {p.TimeMs} / {p.Data.Length}")));
    var result = fragment.Convert(dovi);
    Check(result.Rpus > 0, "actual Profile 7 RPUs converted");
    var bl = fragment.Tracks.Where(t => t.Type == 1).MaxBy(t => t.Width)!;
    byte[] Pictures(IEnumerable<MatroskaFragment.Packet> packets) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in packets.Where(p => p.Track == bl.Number)) foreach (var nal in MatroskaFragment.Nals(p.Data, 4))
            if ((nal.Span[0] >> 1 & 63) <= 31) hash.AppendData(nal.Span);
        return hash.GetHashAndReset();
    }
    Check(Pictures(fragment.Packets).SequenceEqual(Pictures(result.Packets)), "compressed base-layer pictures unchanged");
    foreach (var track in fragment.Tracks.Where(t => t.Type != 1))
    {
        var before = fragment.Packets.Where(p => p.Track == track.Number).ToArray(); var after = result.Packets.Where(p => p.Track == track.Number).ToArray();
        Check(before.Length == after.Length && before.Zip(after).All(p => p.First == p.Second), "non-video packets/timestamps unchanged track " + track.Number);
    }
    var origin = Math.Max(0, result.Packets.Min(p => p.TimeMs));
    var ebml = Ebml.Bytes(0x1a45dfa3, Ebml.Number(0x4286, 1), Ebml.Number(0x42f7, 1), Ebml.Number(0x42f2, 4), Ebml.Number(0x42f3, 8), Ebml.Text(0x4282, "matroska"), Ebml.Number(0x4287, 4), Ebml.Number(0x4285, 2));
    var info = Ebml.Bytes(0x1549a966, Ebml.Number(0x2ad7b1, 1000000), Ebml.Text(0x4d80, "EmbyBlurayHelper"), Ebml.Text(0x5741, "EmbyBlurayHelper"), Ebml.Float(0x4489, result.Packets.Max(p => p.TimeMs) - origin));
    File.WriteAllBytes(args[2], Ebml.Join([ebml, Ebml.Bytes(0x18538067, info, result.Tracks, MatroskaFragment.Cluster(result.Packets, origin))]));
    Console.WriteLine($"Wrote {args[2]}; {result.Rpus} pictures, {result.Packets.Count} packets.");
}
Console.WriteLine($"{checks} checks passed.");
