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
    // Item, name and reason are display-only, for the overview; Reason is set
    // only for disc items that keep the original playback.
    internal sealed record Source(string ItemId, string Name, string SourceId, bool Disc);
    private sealed record Entry(Listing? Listing, DateTime Saved, string? ItemId = null, string? Name = null, string? SourceId = null, string? Reason = null);
    internal sealed record Unresolved(string ItemId, string Name, string Reason);
    private const int MaximumEntries = 50000;
    // Temporary read failures (unmounted volume, permissions) are retried after
    // this long, so a missing disk is not probed on every sync.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);
    private readonly object gate = new();
    private Dictionary<string, Entry>? entries;
    private readonly Dictionary<string, DateTime> deferred = new(StringComparer.Ordinal);
    // Source id -> key used this session, so a superseded record is not reported.
    private readonly Dictionary<string, string> current = new(StringComparer.Ordinal);
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

    internal bool TryGet(string key, Source? source, out Listing? listing)
    {
        lock (gate)
        {
            Load();
            if (entries!.TryGetValue(key, out var entry))
            {
                listing = entry.Listing;
                if (source is not null)
                {
                    current[source.SourceId] = key;
                    // Records written before display fields existed gain them on the next sync.
                    if (entry.SourceId is null) { entries[key] = entry with { ItemId = source.ItemId, Name = source.Name, SourceId = source.SourceId,
                        Reason = entry.Listing is null && entry.Reason is null && source.Disc ? "升级前已判定，原因未记录；Emby 重新扫描或播放后更新" : entry.Reason }; dirty = true; }
                }
                return true;
            }
            listing = null;
            return deferred.TryGetValue(key, out var until) && until > DateTime.UtcNow;
        }
    }

    internal void Put(string key, Listing? listing, Source? source = null, string? reason = null)
    {
        lock (gate)
        {
            Load(); deferred.Remove(key);
            if (source is not null) current[source.SourceId] = key;
            if (entries!.TryGetValue(key, out var old) && old.Listing == listing && old.SourceId is not null) return;
            entries[key] = new(listing, DateTime.UtcNow, source?.ItemId, source?.Name, source?.SourceId, listing is null ? reason : null); dirty = true;
        }
    }

    // One row per disc source: the record in use this session, otherwise the
    // most recently saved one. Items whose sources all keep the original
    // playback are listed with the reason.
    private Entry[] Latest()
    {
        var all = entries!;
        return all.Where(e => e.Value.SourceId is not null).GroupBy(e => e.Value.SourceId!)
            .Select(g => current.TryGetValue(g.Key, out var key) && all.TryGetValue(key, out var used) ? used : g.MaxBy(e => e.Value.Saved).Value).ToArray();
    }

    // What Infuse was last told about each source of one item; read-only.
    internal IReadOnlyList<(string SourceId, Listing? Listing, string? Reason)> ForItem(string itemId)
    {
        lock (gate) { Load(); return Latest().Where(e => e.ItemId == itemId).Select(e => (e.SourceId!, e.Listing, e.Reason)).ToArray(); }
    }

    internal (int Available, IReadOnlyList<Unresolved> Unavailable) Summary()
    {
        lock (gate)
        {
            Load();
            var latest = Latest();
            var available = latest.Where(e => e.Listing is not null).Select(e => e.ItemId).ToHashSet();
            var unavailable = latest.Where(e => e.Listing is null && e.Reason is not null && !available.Contains(e.ItemId))
                .GroupBy(e => e.ItemId).Select(g => g.First()).Select(e => new Unresolved(e.ItemId!, e.Name ?? e.ItemId!, e.Reason!))
                .OrderBy(e => e.Name, StringComparer.CurrentCulture).Take(200).ToArray();
            return (available.Count, unavailable);
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
