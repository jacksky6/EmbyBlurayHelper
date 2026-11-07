using System.Diagnostics;
using EmbyBlurayHelper.ProbeWrapper;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

internal sealed record DvFragment(byte[] Cluster, byte[] Tracks, int VideoTrack, int Pictures, long InputBytes, long ElapsedMs);

internal sealed class DvFragmentBuilder(string ffmpeg, string libdovi, Action<MatroskaFragment, IReadOnlyList<MatroskaFragment.Packet>>? verify = null)
{
    // A global admission gate bounds native processes and transformation scratch
    // memory independently of the configurable media cache.
    private static readonly SemaphoreSlim Worker = new(1, 1);
    internal async Task<DvFragment> Build(DvDiscPlan plan, DvDiscPlan.Segment segment, CancellationToken token)
    {
        await Worker.WaitAsync(token);
        try
        {
            var clock = Stopwatch.StartNew(); using var input = plan.Open(segment);
            await using var source = new IsoMediaHttpSource(input.Slice, token, readBudget: 256L * 1024 * 1024);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var info = EmbyToolProcess.CreateStartInfo(ffmpeg);
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-copyts", "-probesize", "8000000", "-analyzeduration", "3000000",
                "-f", "mpegts", "-i", source.Url, "-map", "0:v", "-map", "0:a?", "-map", "0:s?", "-c", "copy", "-avoid_negative_ts", "disabled",
                "-fflags", "+bitexact", "-max_interleave_delta", "1000000", "-cluster_time_limit", "1000", "-f", "matroska", "pipe:1" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Unable to start the DV remuxer.");
            using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = ReadBounded(process.StandardError.BaseStream, 64 * 1024, timeout.Token, discardOverflow: true);
            ReadOnlyMemory<byte> bytes;
            try
            {
                bytes = await ReadBounded(process.StandardOutput.BaseStream, 192 * 1024 * 1024, timeout.Token);
                await process.WaitForExitAsync(timeout.Token); var stderr = await errors;
                var message = System.Text.Encoding.UTF8.GetString(stderr.Span);
                if (process.ExitCode != 0) throw new IOException("DV fragment remux failed (exit " + process.ExitCode + "). " + message[..Math.Min(message.Length, 1200)]);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                try { await errors; } catch (OperationCanceledException) { }
                token.ThrowIfCancellationRequested(); throw;
            }
            var fragment = MatroskaFragment.Parse(bytes);
            Select(fragment, input.StartMs, input.EndMs, segment.End == segment.Clip.Index.Bytes);
            using var converter = new DoviNative(libdovi); var result = fragment.Convert(converter, plan.Level);
            verify?.Invoke(fragment, result.Packets);
            var cluster = MatroskaFragment.Cluster(result.Packets, segment.Clip.InMs - segment.Clip.TimelineMs);
            if (cluster.Length + 9 > segment.SlotBytes) throw new InvalidDataException("Converted GOP exceeds its reserved virtual slot.");
            return new(cluster, result.Tracks, result.Video, result.Rpus, source.BytesRead, clock.ElapsedMilliseconds);
        }
        finally { Worker.Release(); }
    }
    private static void Select(MatroskaFragment fragment, long start, long end, bool last)
    {
        var video = fragment.Tracks.Where(t => t.Type == 1).MaxBy(t => t.Width) ?? throw new InvalidDataException("Missing HEVC base layer.");
        var frames = fragment.Packets.Where(p => p.Track == video.Number).ToArray();
        var first = Array.FindIndex(frames, p => p.TimeMs == start && p.Key);
        var next = last ? frames.Length : Array.FindIndex(frames, first >= 0 ? first + 1 : 0, p => p.TimeMs == end && p.Key);
        if (first < 0 || next <= first) throw new InvalidDataException($"Demuxed GOP does not match CLPI ({start}-{end} ms).");
        var selected = frames[first..next].ToHashSet(); var times = selected.GroupBy(p => p.TimeMs).ToDictionary(g => g.Key, g => g.Count());
        var remaining = new Dictionary<long, int>(times);
        var enhancement = fragment.Tracks.FirstOrDefault(t => t.Type == 1 && t != video)?.Number;
        fragment.Packets.RemoveAll(p =>
        {
            if (p.Track == video.Number) return !selected.Contains(p);
            if (p.Track == enhancement)
            {
                if (!remaining.TryGetValue(p.TimeMs, out var count) || count == 0) return true;
                remaining[p.TimeMs] = count - 1; return false;
            }
            return p.TimeMs < start || p.TimeMs >= end;
        });
        if (enhancement is not null && remaining.Values.Any(n => n != 0)) throw new InvalidDataException("BL/EL frame timestamps differ in this GOP.");
    }
    private static async Task<ReadOnlyMemory<byte>> ReadBounded(Stream stream, int maximum, CancellationToken token, bool discardOverflow = false)
    {
        using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); if (count == 0) break;
            if (output.Length + count > maximum)
            {
                if (!discardOverflow) throw new IOException("DV remuxer exceeded the fragment memory budget.");
                count = Math.Max(0, maximum - (int)output.Length);
            }
            if (output.Length + count > output.Capacity) output.Capacity = Math.Min(maximum, Math.Max((int)output.Length + count, Math.Max(65536, output.Capacity * 2)));
            output.Write(buffer, 0, count);
        }
        return output.GetBuffer().AsMemory(0, checked((int)output.Length));
    }
}
