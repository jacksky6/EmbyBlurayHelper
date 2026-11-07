using EmbyBlurayHelper.Proxy.DolbyVision;
using Microsoft.AspNetCore.Http;

internal static class CoreChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        MatroskaFragment AudioTracks(string firstLanguage = "und")
        {
            var fragment = new MatroskaFragment();
            fragment.Tracks.Add(new(3, 2, "A_TRUEHD", Ebml.Text(0x22b59c, firstLanguage), [], 0));
            fragment.Tracks.Add(new(4, 2, "A_AC3", Ebml.Text(0x22b59c, "und"), [], 0));
            return fragment;
        }
        string Language(MatroskaFragment.Track track) => System.Text.Encoding.ASCII.GetString(Ebml.Read(track.Definition).Single(e => e.Id == 0x22b59c).Data.Span);
        var audio = AudioTracks();
        audio.FillMissingLanguages([new(2, "truehd", "eng"), new(2, "ac3", "zho")]);
        check(Language(audio.Tracks[0]) == "eng" && Language(audio.Tracks[1]) == "zho", "known source language fills the TrueHD compatibility AC3 track");
        audio = AudioTracks("fra"); audio.FillMissingLanguages([new(2, "truehd", "eng"), new(2, "ac3", "zho")]);
        check(Language(audio.Tracks[0]) == "fra", "existing native language takes precedence over saved metadata");
        audio = AudioTracks(); audio.FillMissingLanguages([new(2, "ac3", "eng"), new(2, "truehd", "zho")]);
        check(audio.Tracks.All(t => Language(t) == "und"), "different codec order cannot misassign source languages");
        audio = AudioTracks(); audio.FillMissingLanguages([new(2, "truehd", "und"), new(2, "ac3", "unknown")]);
        check(audio.Tracks.All(t => Language(t) == "und"), "unknown source language stays unknown");
        var context = new DefaultHttpContext();
        foreach (var (input, start, end, ok) in new[] { ("bytes=0-99", 0L, 99L, true), ("bytes=900-", 900L, 999L, true), ("bytes=-12", 988L, 999L, true),
            ("bytes=999-1001", 999L, 999L, true), ("bytes=1000-", 0L, 0L, false), ("bytes=-0", 0L, 0L, false), ("bytes=0-2,5-9", 0L, 0L, false), ("wat", 0L, 0L, false) })
        {
            context.Request.Headers.Range = input;
            var valid = InfuseDolbyVisionProxy.TryRange(context, 1000, "sig", out var actualStart, out var actualEnd, out var partial);
            check(valid == ok && (!ok || partial && actualStart == start && actualEnd == end), "range validation " + input);
        }
        context.Request.Headers.IfRange = "\"old\""; context.Request.Headers.Range = "bytes=100-200";
        check(InfuseDolbyVisionProxy.TryRange(context, 1000, "sig", out var from, out var to, out var part) && from == 0 && to == 999 && !part, "changed If-Range returns a complete representation");
        context.Request.Headers.IfRange = "\"sig\"";
        check(InfuseDolbyVisionProxy.TryRange(context, 1000, "sig", out from, out to, out part) && from == 100 && to == 200 && part, "matching If-Range permits partial content");
        DvFragment Fragment(int size) => new(new byte[size], [], 1, 1, 1, 1);
        using (var cache = new DvSessionCache(100, 100))
        {
            var builds = 0;
            async Task<DvFragment> Build(CancellationToken token) { Interlocked.Increment(ref builds); await Task.Delay(20, token); return Fragment(60); }
            using var first = await cache.Get("a", "1", 60, Build, CancellationToken.None);
            using (var repeat = await cache.Get("a", "1", 60, Build, CancellationToken.None)) check(ReferenceEquals(first.Fragment, repeat.Fragment) && builds == 1, "repeat ranges reuse one cached fragment");
            var blocked = cache.Get("a", "2", 60, Build, CancellationToken.None);
            await Task.Delay(30);
            check(!blocked.IsCompleted && builds == 1, "pinned media stays charged and blocks over-budget admission");
            first.Dispose(); using var next = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
            check(builds == 2, "released least-recent fragment is evicted to admit the next");
        }
        using (var cache = new DvSessionCache(100, 200))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var builds = 0;
            async Task<DvFragment> Build(CancellationToken token) { Interlocked.Increment(ref builds); started.TrySetResult(); await finish.Task.WaitAsync(token); return Fragment(50); }
            using var cancelled = new CancellationTokenSource();
            var first = cache.Get("a", "1", 50, Build, cancelled.Token); await started.Task;
            var second = cache.Get("a", "1", 50, Build, CancellationToken.None);
            cancelled.Cancel(); try { using var unused = await first; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            finish.SetResult(); using var result = await second;
            check(builds == 1, "one cancelled range does not stop a shared conversion still in use");
        }
        using (var cache = new DvSessionCache(100, 100))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<DvFragment> Build(CancellationToken token)
            { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { stopped.TrySetResult(); } return Fragment(50); }
            using var cancel = new CancellationTokenSource(); var task = cache.Get("a", "1", 50, Build, cancel.Token); await started.Task; cancel.Cancel();
            try { using var unused = await task; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)); check(true, "last cancelled reader stops its conversion");
        }
        using (var cache = new DvSessionCache(100, 100))
        {
            var fail = true; Task<DvFragment> Build(CancellationToken _) => fail ? throw new IOException("fixture failure") : Task.FromResult(Fragment(50));
            try { using var unused = await cache.Get("a", "1", 50, Build, CancellationToken.None); throw new Exception("Expected failure"); } catch (IOException) { }
            fail = false; using var retry = await cache.Get("a", "1", 50, Build, CancellationToken.None);
            check(true, "failed conversion releases reservations and permits retry");
        }
        foreach (var bytes in new[] { new byte[] { 0 }, new byte[] { 0xa3, 0x84, 1 }, new byte[] { 0xa3, 0 } })
        { try { _ = Ebml.Read(bytes).ToArray(); throw new Exception("Expected malformed EBML rejection"); } catch (InvalidDataException) { check(true, "malformed EBML fails closed"); } }
    }
}
