using EmbyBlurayHelper.Proxy.DolbyVision;

internal static class ProbeRetryChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var probes = new List<int>(); var warnings = 0;
        var value = await DvFragmentBuilder.WithProbeRetry((probe, _) =>
        {
            probes.Add(probe); return Task.FromResult("complete fragment");
        }, _ => warnings++, CancellationToken.None);
        check(value == "complete fragment" && probes.SequenceEqual(new[] {16_000_000}) && warnings == 0,
            "successful segments use 16 MB without a second remux");

        probes.Clear(); using var lifetime = new CancellationTokenSource();
        value = await DvFragmentBuilder.WithProbeRetry((probe, token) =>
        {
            check(token == lifetime.Token, "both probe attempts share the segment cancellation and deadline");
            probes.Add(probe);
            return probe == 16_000_000
                ? Task.FromException<string>(new DvFragmentBuilder.RemuxFailure(1, probe, "dimensions not set"))
                : Task.FromResult("complete replacement fragment");
        }, _ => warnings++, lifetime.Token);
        check(value == "complete replacement fragment" && probes.SequenceEqual(new[] {16_000_000, 32_000_000}) && warnings == 1,
            "a remux failure retries once at 32 MB and returns only the successful result");

        probes.Clear(); DvFragmentBuilder.RemuxFailure? finalFailure = null;
        try
        {
            await DvFragmentBuilder.WithProbeRetry<string>((probe, _) =>
            {
                probes.Add(probe); return Task.FromException<string>(new DvFragmentBuilder.RemuxFailure(1, probe, "fixture failure"));
            }, null, CancellationToken.None);
        }
        catch (DvFragmentBuilder.RemuxFailure e) { finalFailure = e; }
        check(probes.SequenceEqual(new[] {16_000_000, 32_000_000}) && finalFailure?.Message.Contains("32000000") == true,
            "failure at 32 MB is reported without a third attempt or partial success");

        foreach (var failure in new Exception[] {new IOException("input read budget exceeded"), new InvalidDataException("invalid media"), new OperationCanceledException("deadline exceeded")})
        {
            probes.Clear(); Exception? observed = null;
            try
            {
                await DvFragmentBuilder.WithProbeRetry<string>((probe, _) =>
                {
                    probes.Add(probe); return Task.FromException<string>(failure);
                }, null, CancellationToken.None);
            }
            catch (Exception e) { observed = e; }
            check(ReferenceEquals(observed, failure) && probes.SequenceEqual(new[] {16_000_000}),
                "unrelated input errors and timeouts do not restart conversion: " + failure.GetType().Name);
        }

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); probes.Clear();
        try
        {
            await DvFragmentBuilder.WithProbeRetry((probe, _) => {probes.Add(probe); return Task.FromResult(1);}, null, cancelled.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) {check(probes.Count == 0, "an already cancelled range starts no remuxer");}

        using var duringFailure = new CancellationTokenSource(); probes.Clear(); warnings = 0;
        try
        {
            await DvFragmentBuilder.WithProbeRetry<int>((probe, _) =>
            {
                probes.Add(probe); duringFailure.Cancel();
                return Task.FromException<int>(new DvFragmentBuilder.RemuxFailure(1, probe, "process killed on cancellation"));
            }, _ => warnings++, duringFailure.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException)
        {
            check(probes.SequenceEqual(new[] {16_000_000}) && warnings == 0,
                "a cancelled first attempt never starts or advertises a retry");
        }

        using var beforeRetry = new CancellationTokenSource(); probes.Clear();
        try
        {
            await DvFragmentBuilder.WithProbeRetry<int>((probe, _) =>
            {
                probes.Add(probe); return Task.FromException<int>(new DvFragmentBuilder.RemuxFailure(1, probe, "fixture failure"));
            }, _ => beforeRetry.Cancel(), beforeRetry.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException)
        {
            check(probes.SequenceEqual(new[] {16_000_000}), "cancellation between attempts prevents the 32 MB process");
        }
    }
}
