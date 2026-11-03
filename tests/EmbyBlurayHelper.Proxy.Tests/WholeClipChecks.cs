using System.Buffers.Binary;
using System.Text;
using EmbyBlurayHelper.Proxy;

internal static class WholeClipChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "whole-clip-checks-" + Guid.NewGuid());
        var bdmv = Path.Combine(root, "BDMV");
        foreach (var name in new[] { "PLAYLIST", "CLIPINF", "STREAM" }) Directory.CreateDirectory(Path.Combine(bdmv, name));
        var playlistPath = Path.Combine(bdmv, "PLAYLIST", "00001.mpls");
        var clipPath = Path.Combine(bdmv, "CLIPINF", "00002.clpi");
        var streamPath = Path.Combine(bdmv, "STREAM", "00002.m2ts");
        var playlist = new byte[192];
        Encoding.ASCII.GetBytes("MPLS0300", playlist);
        U32(playlist, 8, 64); U32(playlist, 64, 74); U16(playlist, 70, 2);
        var all = new List<byte>();
        var random = new Random(1234);
        for (var index = 0; index < 2; index++)
        {
            var id = (index + 1).ToString("D5");
            var item = 76 + index * 34;
            U16(playlist, item - 2, 32);
            Encoding.ASCII.GetBytes(id + "M2TS", playlist.AsSpan(item));
            U16(playlist, item + 9, index == 0 ? (ushort)1 : (ushort)5);
            U32(playlist, item + 12, 45000); U32(playlist, item + 16, 90001);
            var clip = new byte[128];
            Encoding.ASCII.GetBytes("HDMV0300", clip);
            U32(clip, 8, 64); clip[46] = 1; clip[47] = 1; clip[51] = 1; U32(clip, 56, 64);
            U32(clip, 64, 22); clip[69] = 1; clip[74] = 1;
            U16(clip, 76, 0x1001); U32(clip, 82, 45000); U32(clip, 86, 90000);
            File.WriteAllBytes(Path.Combine(bdmv, "CLIPINF", id + ".clpi"), clip);
            var bytes = new byte[192 * 64]; random.NextBytes(bytes);
            for (var at = 0; at < bytes.Length; at += 192) { bytes[at + 4] = 0x47; bytes[at + 7] = 0x10; }
            File.WriteAllBytes(Path.Combine(bdmv, "STREAM", id + ".m2ts"), bytes);
            all.AddRange(bytes);
        }
        File.WriteAllBytes(playlistPath, playlist);
        try
        {
            var plan = WholeClipPlaylist.TryCreate(root, 1, out _)!;
            Check(plan is not null && plan.Length == all.Count && plan.Clips.Count == 2, "MPLS and CLPI prove two complete clips");
            foreach (var offset in new long[] { 0, 123, 192 * 64 - 17, 192 * 64, all.Count - 37 })
            {
                await using var stream = plan!.Open(offset);
                using var output = new MemoryStream(); await stream.CopyToAsync(output);
                Check(output.ToArray().SequenceEqual(all.Skip((int)offset)), "Raw seek/cross-clip bytes match originals: " + offset);
                Check(await stream.ReadAsync(new byte[1]) == 0, "End of title is stable: " + offset);
            }
            await using (var stream = plan!.Open(0))
            {
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await stream.ReadAsync(new byte[1], cancel.Token); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { checks++; }
            }
            void Reject(byte[] changed, string message)
            {
                File.WriteAllBytes(playlistPath, changed);
                Check(WholeClipPlaylist.TryCreate(root, 1, out _) is null, message);
                File.WriteAllBytes(playlistPath, playlist);
            }
            var trimmedIn = playlist.ToArray(); U32(trimmedIn, 88, 45001); Reject(trimmedIn, "Partial in-point is not concatenated as a whole clip");
            var trimmedOut = playlist.ToArray(); U32(trimmedOut, 92, 89999); Reject(trimmedOut, "Partial out-point is rejected");
            var multiAngle = playlist.ToArray(); multiAngle[86] |= 0x10; Reject(multiAngle, "Multi-angle title requires native reader");
            var subPath = playlist.ToArray(); U16(subPath, 72, 1); Reject(subPath, "Secondary path requires native reader");
            var nonSeamless = playlist.ToArray(); U16(nonSeamless, 119, 1); Reject(nonSeamless, "Later non-seamless clip requires native reader");
            var stc = File.ReadAllBytes(clipPath); var extraStc = stc.ToArray(); extraStc[74] = 2; File.WriteAllBytes(clipPath, extraStc);
            Check(WholeClipPlaylist.TryCreate(root, 1, out _) is null, "Multiple STCs are not guessed"); File.WriteAllBytes(clipPath, stc);
            var source = File.ReadAllBytes(streamPath);
            File.WriteAllBytes(streamPath, source[..^192]);
            try { WholeClipPlaylist.TryCreate(root, 1, out _); throw new Exception("Truncated file was accepted"); }
            catch (InvalidDataException) { checks++; }
            File.WriteAllBytes(streamPath, source);
            var encrypted = source.ToArray(); encrypted[7] |= 0x80; File.WriteAllBytes(streamPath, encrypted);
            try { using var stream = plan.Open(0); throw new Exception("Encrypted transport was accepted"); }
            catch (InvalidDataException) { checks++; }
            File.WriteAllBytes(streamPath, source);
            var afterFailure = File.Open(streamPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); afterFailure.Dispose();
            Check(true, "Failed preflight releases previously opened clip handles");
            File.Move(streamPath, Path.ChangeExtension(streamPath, ".M2TS"));
            Check(WholeClipPlaylist.TryCreate(root, 1, out _) is not null, "Uppercase extensions work on Linux");
        }
        finally { Directory.Delete(root, true); }
        return checks;
    }

    private static void U32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), value);
    private static void U16(byte[] data, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), value);
}
