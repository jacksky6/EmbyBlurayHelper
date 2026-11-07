using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbyBlurayHelper.App;

// Read-only catalog and format rules, separate from job scheduling and probing.
internal sealed class MediaFeatureCatalog(SettingsStore settings, HttpClient client)
{
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Connection(HelperSettings config) => Hash(SettingsStore.PlanConnection(config));
    internal static string Engine(HelperSettings config)
    {
        static string Stamp(string path)
        {
            if (!File.Exists(path)) return path;
            var file = new FileInfo(path);
            return file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        return Hash(Stamp(config.FeatureProbePath) + "|" + Stamp(config.UpstreamProbePath));
    }
    internal void EnsureContext(HelperSettings config, bool probing = false)
    {
        var current = settings.Current;
        if (Connection(current) != Connection(config) || (probing &&
            (!current.EnableMediaFeatures || Engine(current) != Engine(config))))
            throw new InvalidOperationException("Emby 连接或规格探测配置已变更，任务已停止；请重新检查媒体库。");
    }
    internal async Task<JsonElement> Read(HelperSettings config, string route, CancellationToken token)
    {
        EnsureContext(config);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, config.EmbyUrl.TrimEnd('/') + "/emby/" + route);
        request.Headers.Add("X-Emby-Token", config.EmbyApiKey);
        using var response = await client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw await EmbyRequestFailure.From(response, HttpMethod.Get, route, config.EmbyApiKey, timeout.Token);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
    }
    internal async Task<List<FeatureLibrary>> Libraries(HelperSettings config, CancellationToken token)
    {
        var raw = await Read(config, "Library/VirtualFolders", token);
        if (raw.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Emby 未返回媒体库列表。");
        var groups = raw.EnumerateArray().Where(l => SettingsStore.LibraryTypeAllowed(Text(l, "CollectionType")))
            .Select(l => new FeatureLibrary { Id = Text(l, "ItemId"), Name = Text(l, "Name") }).ToList();
        if (groups.Any(l => l.Id.Length == 0) || groups.Select(l => l.Id).Distinct().Count() != groups.Count)
            throw new InvalidDataException("Emby 媒体库 ID 缺失或重复，无法生成可靠计划。");
        var server = await Read(config, "System/Info/Public", token);
        var serverId = Text(server, "Id");
        if (serverId.Length == 0) throw new InvalidDataException("Emby 未返回服务器 ID。");
        return groups.Where(l => SettingsStore.LibraryAllowed(config, serverId, l.Id)).ToList();
    }
    internal async Task Movies(HelperSettings config, string libraryId, Action<JsonElement> receive, CancellationToken token, Action? pageCompleted = null)
    {
        if (!(await Libraries(config, token)).Any(l => l.Id == libraryId))
            throw new InvalidOperationException("该媒体库已排除、已删除或属于系统合集，请重新检查。");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; ;)
        {
            token.ThrowIfCancellationRequested();
            var page = await Read(config, $"Items?ParentId={Uri.EscapeDataString(libraryId)}&Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams,Path&SortBy=SortName&SortOrder=Ascending&StartIndex={offset}&Limit=200", token);
            if (!page.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Emby 未返回完整电影列表。");
            var total = page.TryGetProperty("TotalRecordCount", out var count) && count.TryGetInt32(out var number) ? number : 0;
            var added = 0;
            foreach (var item in items.EnumerateArray())
            {
                var id = Text(item, "Id");
                if (id.Length == 0) throw new InvalidDataException("Emby 返回了没有 ID 的电影。");
                if (!visited.Add(id)) continue;
                added++; receive(item);
            }
            pageCompleted?.Invoke();
            if (items.GetArrayLength() == 0)
            {
                if (offset < total) throw new InvalidDataException("Emby 分页提前结束，请重新检查媒体库。");
                break;
            }
            if (added == 0) throw new InvalidDataException("Emby 分页没有前进，请重新检查媒体库。");
            offset += items.GetArrayLength();
            if (total > 0 && offset >= total) break;
        }
    }
    internal async Task<FeatureMovie?> Movie(HelperSettings config, string id, CancellationToken token)
    {
        var page = await Read(config, "Items?Ids=" + Uri.EscapeDataString(id) + "&Fields=MediaSources,MediaStreams,Path", token);
        if (!page.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Emby 未返回影片媒体源信息。");
        return items.EnumerateArray().Where(i => Text(i, "Id") == id).Select(Analyze).FirstOrDefault();
    }
    internal static FeatureMovie? Analyze(JsonElement item)
    {
        var sources = item.TryGetProperty("MediaSources", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? list.EnumerateArray().ToArray() : [item];
        var movie = new FeatureMovie { Id = Text(item, "Id"), Name = Text(item, "Name") };
        foreach (var source in sources)
        {
            var path = Text(source, "Path");
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var extension = Path.GetExtension(normalized).ToLowerInvariant();
            var kind = extension == ".iso" ? "ISO" : "";
            // A loose M2TS or ordinary file inside a BDMV directory is still a file,
            // not a folder disc. Never infer its eligibility from its parent name.
            if (kind.Length == 0 && extension is not (".mkv" or ".mp4" or ".m2ts" or ".ts" or ".avi" or ".mov" or ".wmv" or ".strm" or ".m4v" or ".webm"))
            {
                var typed = Text(source, "Container").Equals("bluray", StringComparison.OrdinalIgnoreCase) ||
                    Text(source, "VideoType").Equals("Bluray", StringComparison.OrdinalIgnoreCase) ||
                    (path == Text(item, "Path") && Text(item, "VideoType").Equals("Bluray", StringComparison.OrdinalIgnoreCase));
                if (typed || normalized.EndsWith("/BDMV/index.bdmv", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith("/BDMV", StringComparison.OrdinalIgnoreCase)) kind = "BDMV";
            }
            if (kind.Length == 0 || path.Length == 0 || Text(source, "Id").Length == 0) continue;
            var signature = new JsonObject { ["Id"] = Text(source, "Id"), ["Path"] = path };
            foreach (var property in new[] { "Size", "RunTimeTicks" })
                if (source.TryGetProperty(property, out var value)) signature[property] = JsonNode.Parse(value.GetRawText());
            if (source.TryGetProperty("MediaStreams", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                signature["Tracks"] = new JsonArray(tracks.EnumerateArray().Select(s => (JsonNode)NormalizedTrack(s)).ToArray());
            movie.Sources.Add(new FeatureSource { Id = Text(source, "Id"), Path = path, Kind = kind,
                Signature = Hash(signature.ToJsonString()), Details = Describe(source), Raw = source.Clone() });
        }
        if (movie.Sources.Select(s => s.Id).Distinct().Count() != movie.Sources.Count)
            throw new InvalidDataException("原盘媒体源 ID 重复，无法安全生成规格计划。");
        return movie.Sources.Count > 0 ? movie : null;
    }
    private static JsonObject NormalizedTrack(JsonElement stream)
    {
        var result = new JsonObject();
        foreach (var field in new[] { "Index", "Type", "Codec", "Profile", "Title", "Language", "Channels", "ChannelLayout", "Width", "Height", "BitDepth", "ColorTransfer", "IsExternal", "Path", "ExtendedVideoType", "ExtendedVideoSubType" })
            if (stream.TryGetProperty(field, out var value)) result[field] = JsonNode.Parse(value.GetRawText());
        return result;
    }
    internal static string Describe(JsonElement source)
    {
        if (!source.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array) return "尚无规格信息";
        var confirmed = new List<string>();
        foreach (var stream in streams.EnumerateArray())
        {
            var index = stream.TryGetProperty("Index", out var i) ? i.ToString() : "?";
            var profile = Text(stream, "Profile");
            if (profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase)) confirmed.Add("音轨 #" + index + " · " + profile);
            if (Text(stream, "ExtendedVideoType") == "DolbyVision") confirmed.Add("视频 #" + index + " · " +
                (Text(stream, "Title").Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase) ? Text(stream, "Title") : "Dolby Vision " + Text(stream, "ExtendedVideoSubType")));
        }
        return confirmed.Count > 0 ? string.Join("；", confirmed) : "当前未显示 Atmos / Dolby Vision";
    }
    internal static bool Preserved(FeatureSource before, FeatureSource after)
    {
        if (before.Id != after.Id || before.Path != after.Path) return false;
        foreach (var field in new[] { "RunTimeTicks", "Size" })
            if (before.Raw.TryGetProperty(field, out var old) && (!after.Raw.TryGetProperty(field, out var current) || old.ToString() != current.ToString())) return false;
        if (!before.Raw.TryGetProperty("MediaStreams", out var saved) || saved.ValueKind != JsonValueKind.Array) return true;
        if (!after.Raw.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array || saved.GetArrayLength() != streams.GetArrayLength()) return false;
        foreach (var old in saved.EnumerateArray())
        {
            var index = old.GetProperty("Index").ToString();
            var matches = streams.EnumerateArray().Where(s => s.GetProperty("Index").ToString() == index).ToArray();
            if (matches.Length != 1) return false;
            foreach (var field in NormalizedTrack(old))
            {
                if (field.Key is "Profile" or "ExtendedVideoType" or "ExtendedVideoSubType" ||
                    (field.Key == "Title" && string.IsNullOrWhiteSpace(field.Value?.ToString()))) continue;
                if (!matches[0].TryGetProperty(field.Key, out var value) || !JsonNode.DeepEquals(field.Value, JsonNode.Parse(value.GetRawText()))) return false;
            }
        }
        return true;
    }
    internal static bool SavedFeatures(FeatureSource after, JsonElement response)
    {
        if (!response.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
            Text(response, "mediaSourceId") != after.Id || !response.TryGetProperty("streams", out var expected) || expected.ValueKind != JsonValueKind.Array ||
            !after.Raw.TryGetProperty("MediaStreams", out var saved) || saved.ValueKind != JsonValueKind.Array) return false;
        foreach (var detected in expected.EnumerateArray())
        {
            var matches = saved.EnumerateArray().Where(s => s.GetProperty("Index").ToString() == detected.GetProperty("Index").ToString()).ToArray();
            if (matches.Length != 1) return false;
            foreach (var field in new[] { "Codec", "Profile", "Title", "ExtendedVideoType", "ExtendedVideoSubType" })
            {
                var value = Text(detected, field);
                if (value.Length > 0 && Text(matches[0], field) != value) return false;
            }
        }
        return expected.GetArrayLength() > 0;
    }
}
