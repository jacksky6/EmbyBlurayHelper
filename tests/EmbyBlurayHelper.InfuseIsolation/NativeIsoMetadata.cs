using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EmbyBlurayHelper.Proxy.Chapters;

// Test-only metadata control. Keep the displayed item identity but use the
// actual ISO's media source, including its real path, tracks and duration.
// It is applied only to client responses, never to playback authorization.
internal sealed class NativeIsoMetadata(JsonElement donor, Func<JsonElement, DiscChapterPresentation?>? virtualPresentation = null)
{
    private readonly JsonObject donorItem = JsonNode.Parse(donor.GetRawText())!.AsObject();
    internal static readonly Regex Route = new("^(?:/emby)?/(?:Items/[^/]+/PlaybackInfo|(?:Users/[^/]+/)?Items(?:/[^/]+)?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal byte[]? Rewrite(string path, byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) return null;
        var root = JsonNode.Parse(bytes);
        var changed = false;
        if (root is JsonObject obj)
        {
            if (obj["Items"] is JsonArray items)
                foreach (var entry in items.OfType<JsonObject>()) changed |= RewriteItem(path, entry);
            else changed = RewriteItem(path, obj);
        }
        else if (root is JsonArray items)
            foreach (var entry in items.OfType<JsonObject>()) changed |= RewriteItem(path, entry);
        return changed ? Encoding.UTF8.GetBytes(root!.ToJsonString()) : null;
    }

    private bool RewriteItem(string path, JsonObject target)
    {
        var itemId = target["Id"]?.GetValue<string>() ?? Regex.Match(path, "(?i)/Items/([^/]+)").Groups[1].Value;
        if (itemId != "12") return false;
        var donorSource = donorItem["MediaSources"]!.AsArray()[0]!.AsObject();
        JsonObject? presentedSource = null;
        var changed = false;
        if (target["MediaSources"] is JsonArray sources)
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] is not JsonObject source || source["Id"]?.GetValue<string>() != "mediasource_12") continue;
                var native = (virtualPresentation is null ? donorSource : source).DeepClone().AsObject();
                if (virtualPresentation is not null)
                {
                    var presentation = virtualPresentation(source.Deserialize<JsonElement>());
                    if (presentation is null) continue;
                    var originalPath = source["Path"]!.GetValue<string>();
                    var named = EmbyBlurayHelper.Proxy.VirtualMediaName.Chapters(originalPath);
                    native["Path"] = named.Path; native["Name"] = named.FileName;
                    native["Container"] = "blurayiso";
                    native["Size"] = presentation.Image.Length;
                    native["SupportsDirectPlay"] = true;
                    native["SupportsDirectStream"] = true;
                }
                native["Id"] = "mediasource_12";
                if (native.ContainsKey("ItemId")) native["ItemId"] = "12";
                sources[i] = native;
                presentedSource = native;
                changed = true;
            }
        var fields = virtualPresentation is null ? new[] { "Container", "Path", "Size", "RunTimeTicks", "Bitrate", "MediaStreams", "Chapters" } : new[] { "Container", "Path", "Size" };
        foreach (var name in fields)
        {
            if (!target.ContainsKey(name)) continue;
            var value = virtualPresentation is null ? donorItem[name] ?? donorSource[name] : presentedSource?[name];
            if (value is null) continue;
            target[name] = value.DeepClone();
            changed = true;
        }
        return changed;
    }
}
