namespace EmbyBlurayHelper.Proxy.DolbyVision;

// Budgets include resident clusters and reservations for builders. A response
// pins its cluster, so slow clients cannot leave evicted, unaccounted arrays.
internal sealed class DvSessionCache(long perSession, long global) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, Job> jobs = [];
    private readonly Dictionary<string, long> sessionBytes = [];
    private TaskCompletionSource changed = Signal();
    private readonly CancellationTokenSource stop = new();
    private long used; private bool disposed;
    internal CancellationToken Stopping => stop.Token;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Entry(string session, DvFragment data, long bytes)
    { internal string Session = session; internal DvFragment Data = data; internal long Bytes = bytes; internal int Pins; internal DateTime Access = DateTime.UtcNow; }
    private sealed class Job(string session, long reservation, CancellationTokenSource cancel)
    { internal string Session = session; internal long Reservation = reservation; internal CancellationTokenSource Cancel = cancel; internal Task Task = null!; internal int Waiters; }
    internal sealed class Lease(DvFragment fragment, Action release) : IDisposable
    { internal DvFragment Fragment { get; } = fragment; private Action? done = release; public void Dispose() => Interlocked.Exchange(ref done, null)?.Invoke(); }
    internal object Status() { lock (gate) { var reserved = jobs.Values.Sum(j => j.Reservation); return new { cachedMiB = Math.Round((used - reserved) / 1048576d, 1), reservedMiB = Math.Round(reserved / 1048576d, 1), fragments = entries.Count, processing = jobs.Count, sessions = sessionBytes.Count }; } }
    internal async Task<Lease> Get(string session, string id, int reservation, Func<CancellationToken, Task<DvFragment>> build, CancellationToken token)
    {
        if (reservation <= 0 || reservation > perSession || reservation > global) throw new IOException("DV segment exceeds configured cache budget.");
        var key = session + ":" + id;
        while (true)
        {
            token.ThrowIfCancellationRequested(); Task? wait = null; Job? job = null;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this); Prune();
                if (entries.TryGetValue(key, out var entry))
                {
                    entry.Pins++; entry.Access = DateTime.UtcNow;
                    return new(entry.Data, () => { lock (gate) { entry.Pins--; entry.Access = DateTime.UtcNow; Pulse(); } });
                }
                if (jobs.TryGetValue(key, out job)) job.Waiters++;
                else if (MakeRoom(session, reservation))
                {
                    Add(session, reservation); job = new(session, reservation, CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) { Waiters = 1 };
                    jobs.Add(key, job); var pending = job;
                    job.Task = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await build(pending.Cancel.Token);
                            var size = result.Cluster.LongLength + result.Tracks.LongLength;
                            if (size > pending.Reservation) throw new IOException("DV cache reservation exceeded.");
                            lock (gate)
                            {
                                pending.Cancel.Token.ThrowIfCancellationRequested();
                                Add(session, size - pending.Reservation); pending.Reservation = 0;
                                entries.Add(key, new(session, result, size));
                            }
                        }
                        finally
                        {
                            lock (gate) { Add(session, -pending.Reservation); jobs.Remove(key); if (pending.Waiters == 0) pending.Cancel.Dispose(); Pulse(); }
                        }
                    }, CancellationToken.None);
                }
                else wait = changed.Task;
            }
            if (wait is not null) { await wait.WaitAsync(token); continue; }
            try { await job!.Task.WaitAsync(token); }
            finally
            {
                lock (gate)
                {
                    job!.Waiters--;
                    if (job.Waiters == 0) { if (!job.Task.IsCompleted) job.Cancel.Cancel(); else job.Cancel.Dispose(); }
                }
            }
        }
    }
    private void Add(string session, long bytes)
    { used += bytes; var amount = sessionBytes.GetValueOrDefault(session) + bytes; if (amount == 0) sessionBytes.Remove(session); else sessionBytes[session] = amount; }
    private bool MakeRoom(string session, int size)
    {
        while (used + size > global || sessionBytes.GetValueOrDefault(session) + size > perSession)
        {
            var own = sessionBytes.GetValueOrDefault(session) + size > perSession;
            var candidate = entries.Where(e => e.Value.Pins == 0 && (!own || e.Value.Session == session)).OrderBy(e => e.Value.Access).FirstOrDefault();
            if (candidate.Value is null) return false;
            entries.Remove(candidate.Key); Add(candidate.Value.Session, -candidate.Value.Bytes);
        }
        return true;
    }
    private void Prune()
    {
        foreach (var pair in entries.Where(e => e.Value.Pins == 0 && e.Value.Access < DateTime.UtcNow.AddMinutes(-2)).ToArray())
        { entries.Remove(pair.Key); Add(pair.Value.Session, -pair.Value.Bytes); }
    }
    internal void Trim() { lock (gate) { Prune(); Pulse(); } }
    private void Pulse() { var signal = changed; changed = Signal(); signal.TrySetResult(); }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; stop.Cancel(); entries.Clear(); Pulse(); }
        // The linked job sources remain alive until each builder has stopped.
    }
}
