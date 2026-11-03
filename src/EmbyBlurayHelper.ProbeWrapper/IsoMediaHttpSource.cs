using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EmbyBlurayHelper.ProbeWrapper;

// A private, short-lived, seekable view of one ISO member. TcpListener avoids
// Windows HttpListener URL ACLs and needs no mount privileges on Linux/DSM.
internal sealed class IsoMediaHttpSource : IAsyncDisposable
{
    private readonly Stream media;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop;
    private readonly string resource = "/" + Guid.NewGuid().ToString("N") + ".m2ts";
    private readonly Task serving;
    private readonly object mediaLock;
    private readonly List<Task> requests = [];
    private readonly long readBudget;
    internal long BytesRead { get; private set; }
    internal string Url { get; }

    internal IsoMediaHttpSource(Stream media, CancellationToken token, object? sharedLock = null, long readBudget = 256L * 1024 * 1024)
    {
        this.media = media;
        mediaLock = sharedLock ?? new object();
        this.readBudget = readBudget;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{resource}";
        serving = Serve();
    }

    private async Task Serve()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                // A seek can open a new connection before closing the old one.
                // Never block the accept loop on a slow/abandoned body transfer.
                requests.RemoveAll(t => t.IsCompletedSuccessfully);
                if (requests.Count >= 8) { client.Dispose(); continue; }
                requests.Add(Handle(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { await Task.WhenAll(requests); }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await Respond(client.GetStream(), timeout.Token); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task Respond(NetworkStream connection, CancellationToken token)
    {
        using var reader = new StreamReader(connection, Encoding.ASCII, false, 1024, leaveOpen: true);
        var request = (await reader.ReadLineAsync(token))?.Split(' ');
        string? range = null;
        var headerBytes = 0;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
        {
            if ((headerBytes += header.Length) > 8192) return;
            if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = header[6..].Trim();
        }
        if (request is not { Length: 3 } || request[1] != resource || request[0] is not ("GET" or "HEAD"))
        {
            await Header(connection, "404 Not Found", "Content-Length: 0\r\n", token);
            return;
        }
        var length = media.Length;
        var start = 0L;
        var end = length - 1;
        if (range is not null && !TryRange(range, length, out start, out end))
        {
            await Header(connection, "416 Range Not Satisfiable", $"Content-Range: bytes */{length}\r\nContent-Length: 0\r\n", token);
            return;
        }
        var count = end - start + 1;
        await Header(connection, range is null ? "200 OK" : "206 Partial Content",
            $"Content-Type: video/mp2t\r\nAccept-Ranges: bytes\r\nContent-Length: {count}\r\n" +
            (range is null ? "" : $"Content-Range: bytes {start}-{end}/{length}\r\n"), token);
        if (request[0] == "HEAD") return;
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            int read;
            // DiscUtils and its shared image stream are not safe for concurrent seeks.
            lock (mediaLock)
            {
                if (BytesRead >= readBudget) throw new IOException("ISO input exceeded its read budget.");
                media.Position = start;
                read = media.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(count, readBudget - BytesRead)));
                if (read == 0) throw new EndOfStreamException("Truncated ISO member.");
                BytesRead += read;
            }
            await connection.WriteAsync(buffer.AsMemory(0, read), token);
            start += read;
            count -= read;
        }
    }

    internal static bool TryRange(string range, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0 || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = range[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
        }
        else
        {
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length) return false;
            if (parts[1].Length > 0 && (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)) return false;
            end = Math.Min(end, length - 1);
        }
        return true;
    }

    private static Task Header(NetworkStream connection, string status, string fields, CancellationToken token) =>
        connection.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{fields}Connection: close\r\n\r\n"), token).AsTask();

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await serving;
        stop.Dispose();
        // The caller owns the UDF stream and keeps it alive until all requests stop.
    }
}
