using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyBlurayHelper.Proxy.Chapters;

// What Infuse library listings advertise for each disc item. Library syncs never
// touch the media disks once an item is known: an entry changes only when Emby's
// own record for the item changes (a rescan), when the item is actually played,
// or when the virtual disc layout revision changes. Persisted across restarts.
internal sealed class DiscListingStore(string? path, ILogger logger)
{
    internal sealed record Listing(long Length, int ChapterCount, string Playlist, int MetadataBytes);
    private sealed record Entry(Listing? Listing, DateTime Saved);
    private const int MaximumEntries = 50000;
    // Temporary read failures (unmounted volume, permissions) are retried after
    // this long, so a missing disk is not probed on every sync.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);
    private readonly object gate = new();
    private Dictionary<string, Entry>? entries;
    private readonly Dictionary<string, DateTime> deferred = new(StringComparer.Ordinal);
    private bool dirty;

    // Only the Emby fields that decide the virtual disc. Track languages, images
    // and titles are left out, so metadata edits do not trigger a disc re-read.
    internal static string Key(JsonElement source)
    {
        var text = new StringBuilder("rev" + VirtualUdfImage.FormatRevision);
        foreach (var name in new[] { "Id", "Container", "Path", "Size", "RunTimeTicks" })
            text.Append('\n').Append(name).Append('=').Append(source.TryGetProperty(name, out var value) ? value.GetRawText() : "-");
        if (source.TryGetProperty("Chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            foreach (var chapter in chapters.EnumerateArray())
                text.Append('\n').Append(chapter.TryGetProperty("StartPositionTicks", out var start) ? start.GetRawText() : "-")
                    .Append('/').Append(chapter.TryGetProperty("MarkerType", out var marker) ? marker.GetRawText() : "-");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal bool TryGet(string key, out Listing? listing)
    {
        lock (gate)
        {
            Load();
            if (entries!.TryGetValue(key, out var entry)) { listing = entry.Listing; return true; }
            listing = null;
            return deferred.TryGetValue(key, out var until) && until > DateTime.UtcNow;
        }
    }

    internal void Put(string key, Listing? listing)
    {
        lock (gate)
        {
            Load(); deferred.Remove(key);
            if (entries!.TryGetValue(key, out var old) && old.Listing == listing) return;
            entries[key] = new(listing, DateTime.UtcNow); dirty = true;
        }
    }

    internal void Defer(string key) { lock (gate) deferred[key] = DateTime.UtcNow + RetryAfter; }

    internal void Flush()
    {
        if (path is null) return;
        string json;
        lock (gate)
        {
            if (!dirty || entries is null) return;
            if (entries.Count > MaximumEntries)
                foreach (var stale in entries.OrderBy(e => e.Value.Saved).Take(entries.Count - MaximumEntries).Select(e => e.Key).ToArray()) entries.Remove(stale);
            json = JsonSerializer.Serialize(entries); dirty = false;
        }
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (gate) dirty = true;
            logger.LogWarning("Infuse chapters: disc listing record could not be saved ({ErrorType}); it is kept in memory.", e.GetType().Name);
        }
    }

    private void Load()
    {
        if (entries is not null) return;
        entries = new(StringComparer.Ordinal);
        if (path is null || !File.Exists(path)) return;
        try
        {
            foreach (var (key, entry) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []) entries[key] = entry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Infuse chapters: disc listing record unreadable ({ErrorType}); starting a new one.", e.GetType().Name);
        }
    }
}
