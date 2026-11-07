using System.Diagnostics;
using EmbyBlurayHelper.ProbeWrapper;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

internal sealed record DvFragment(byte[] Cluster, byte[] Tracks, int VideoTrack, int Pictures, long InputBytes, long ElapsedMs);

internal sealed class DvFragmentBuilder(string ffmpeg, string libdovi, Action<MatroskaFragment, IReadOnlyList<MatroskaFragment.Packet>>? verify = null, ILogger? logger = null)
{
    internal const int DefaultProbeBytes = 16_000_000;
    internal const int RetryProbeBytes = 32_000_000;
    internal sealed class RemuxFailure(int exitCode, int probeBytes, string detail)
        : IOException($"DV fragment remux failed (exit {exitCode}, probe {probeBytes} bytes). {detail}");

    internal static async Task<T> WithProbeRetry<T>(Func<int, CancellationToken, Task<T>> attempt, Action<RemuxFailure>? retry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await attempt(DefaultProbeBytes, token); }
        catch (RemuxFailure e)
        {
            token.ThrowIfCancellationRequested();
            retry?.Invoke(e);
            token.ThrowIfCancellationRequested();
            return await attempt(RetryProbeBytes, token);
        }
    }

    // A global admission gate bounds native processes and transformation scratch
    // memory independently of the configurable media cache.
    private static readonly SemaphoreSlim Worker = new(1, 1);
    // Reuse the two large workspaces. Repeated MemoryStream growth and one
    // large allocation per picture otherwise leave GiB of collectible buffers
    // in the runtime even though the media cache is correctly bounded.
    private byte[]? remuxScratch;
    private byte[]? pictureScratch;
    private long lastActivity = DateTime.UtcNow.Ticks;
    internal void Trim()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivity) < TimeSpan.FromMinutes(2).Ticks || !Worker.Wait(0)) return;
        try { remuxScratch = null; pictureScratch = null; }
        finally { Worker.Release(); }
    }
    internal async Task<DvFragment> Build(DvDiscPlan plan, DvDiscPlan.Segment segment, CancellationToken token)
    {
        await Worker.WaitAsync(token);
        try
        {
            Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
            var clock = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            long inputBytes = 0, startMs = 0, endMs = 0;
            var probeBytes = DefaultProbeBytes;
            ReadOnlyMemory<byte> bytes;
            try
            {
                bytes = await WithProbeRetry(async (probe, attemptToken) =>
                {
                    probeBytes = probe;
                    attemptToken.ThrowIfCancellationRequested();
                    var remaining = 256L * 1024 * 1024 - inputBytes;
                    if (remaining <= 0) throw new IOException("DV fragment exceeded its shared input read budget.");
                    // Each attempt has a fresh input and HTTP source; partial
                    // FFmpeg output is discarded before restarting the segment.
                    using var input = plan.Open(segment);
                    startMs = input.StartMs; endMs = input.EndMs;
                    var source = new IsoMediaHttpSource(input.Slice, attemptToken, readBudget: remaining);
                    try
                    {
                        await using (source) { return await Remux(source.Url, probe, attemptToken); }
                    }
                    finally { inputBytes += source.BytesRead; }
                }, e => logger?.LogWarning("Infuse DV: playlist {Playlist}; segment {Segment}/{Total}; at {Time} ms; 16 MB remux failed; retrying once with 32 MB probe: {Reason}",
                    plan.Playlist, segment.Number + 1, plan.Segments.Count, segment.ApproxStartMs, e.Message), timeout.Token);
            }
            catch (RemuxFailure e) when (probeBytes == RetryProbeBytes)
            {
                logger?.LogWarning("Infuse DV: playlist {Playlist}; segment {Segment}/{Total}; 32 MB probe retry failed: {Reason}",
                    plan.Playlist, segment.Number + 1, plan.Segments.Count, e.Message);
                throw;
            }
            var fragment = MatroskaFragment.Parse(bytes);
            fragment.FillMissingLanguages(plan.SourceLanguages);
            Select(fragment, startMs, endMs, segment.End == segment.Clip.Index.Bytes);
            using var converter = new DoviNative(libdovi);
            var pictures = pictureScratch ??= GC.AllocateUninitializedArray<byte>(128 * 1024 * 1024);
            var result = fragment.Convert(converter, plan.Level, pictures);
            verify?.Invoke(fragment, result.Packets);
            var cluster = MatroskaFragment.Cluster(result.Packets, segment.Clip.InMs - segment.Clip.TimelineMs);
            if (cluster.Length + 9 > segment.SlotBytes) throw new InvalidDataException("Converted GOP exceeds its reserved virtual slot.");
            if (probeBytes == RetryProbeBytes)
                logger?.LogInformation("Infuse DV: playlist {Playlist}; segment {Segment}/{Total}; 32 MB probe retry succeeded; {Frames} pictures; {Elapsed} ms including first attempt.",
                    plan.Playlist, segment.Number + 1, plan.Segments.Count, result.Rpus, clock.ElapsedMilliseconds);
            return new(cluster, result.Tracks, result.Video, result.Rpus, inputBytes, clock.ElapsedMilliseconds);
        }
        finally { Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks); Worker.Release(); }
    }
    private async Task<ReadOnlyMemory<byte>> Remux(string url, int probeBytes, CancellationToken token)
    {
        var info = EmbyToolProcess.CreateStartInfo(ffmpeg);
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-copyts", "-probesize", probeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), "-analyzeduration", "3000000",
            "-f", "mpegts", "-i", url, "-map", "0:v", "-map", "0:a?", "-map", "0:s?", "-c", "copy", "-avoid_negative_ts", "disabled",
            "-fflags", "+bitexact", "-max_interleave_delta", "1000000", "-cluster_time_limit", "1000", "-f", "matroska", "pipe:1" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Unable to start the DV remuxer.");
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = ReadBounded(process.StandardError.BaseStream, 64 * 1024, token, discardOverflow: true);
        try
        {
            var scratch = remuxScratch ??= GC.AllocateUninitializedArray<byte>(192 * 1024 * 1024);
            var bytes = await ReadInto(process.StandardOutput.BaseStream, scratch, token);
            await process.WaitForExitAsync(token); var stderr = await errors;
            token.ThrowIfCancellationRequested();
            var message = System.Text.Encoding.UTF8.GetString(stderr.Span);
            // Keep the terminal muxer error as well as the initial diagnostic;
            // repeated SPS/PPS warnings can otherwise hide why writing failed.
            if (message.Length > 1200) message = message[..600] + "\n...\n" + message[^600..];
            if (process.ExitCode != 0) throw new RemuxFailure(process.ExitCode, probeBytes, message);
            return bytes;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await errors; } catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested(); throw;
        }
    }
    private static async Task<ReadOnlyMemory<byte>> ReadInto(Stream stream, byte[] buffer, CancellationToken token)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length, Math.Min(65536, buffer.Length - length)), token);
            if (count == 0) return buffer.AsMemory(0, length);
            length += count;
        }
        if (await stream.ReadAsync(new byte[1], token) != 0) throw new IOException("DV remuxer exceeded the fragment memory budget.");
        return buffer;
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
