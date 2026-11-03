using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.Proxy.Chapters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

// Compare both transformations on the exact same native response. No listener,
// settings writes, media scan, playback notification or media modification.
internal static class MetadataParityCheck
{
    internal static async Task Run(string settingsPath)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var upstream = settings.RootElement.GetProperty("embyUrl").GetString()!.TrimEnd('/');
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Port != 8096)
            throw new InvalidOperationException("Comparison is restricted to the local test Emby on 8096.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("X-Emby-Token", settings.RootElement.GetProperty("embyApiKey").GetString());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Infuse-Direct/8.5.9");
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var users = JsonDocument.Parse(await client.GetStringAsync(upstream + "/Users"));
        var user = users.RootElement.EnumerateArray().First(u => u.GetProperty("Policy").GetProperty("IsAdministrator").GetBoolean()).GetProperty("Id").GetString();
        using var donor = JsonDocument.Parse(await client.GetStringAsync(upstream + "/Items?Ids=12&Fields=MediaSources,Chapters,Path"));
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
        var production = new InfuseChapterProxy(new ProxyConfiguration { EnableInfuseChapters = true }, client, cache, NullLogger.Instance);
        var isolation = new NativeIsoMetadata(donor.RootElement.GetProperty("Items")[0], DiscChapterPresentation.Create);
        var routes = new[]
        {
            $"/Users/{user}/Items/12",
            "/Items?Ids=12&Fields=MediaSources,Chapters,Path",
            $"/Users/{user}/Items?Ids=12&Fields=MediaSources,Chapters,Path",
            $"/Items/12/PlaybackInfo?UserId={user}",
            $"/emby/Items/12/PlaybackInfo?UserId={user}"
        };
        foreach (var route in routes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, upstream + route);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Compare(route, await response.Content.ReadAsByteArrayAsync(), "GET");
        }
        var postRoute = $"/Items/12/PlaybackInfo?UserId={user}";
        using var posted = await client.PostAsync(upstream + postRoute, new StringContent("{\"IsPlayback\":false}", System.Text.Encoding.UTF8, "application/json"));
        posted.EnsureSuccessStatusCode();
        Compare(postRoute, await posted.Content.ReadAsByteArrayAsync(), "POST");

        void Compare(string route, byte[] original, string method)
        {
            var endpoint = new Uri(upstream + route);
            var context = new DefaultHttpContext();
            context.Request.Path = endpoint.AbsolutePath;
            context.Request.QueryString = new QueryString(endpoint.Query);
            context.Request.Method = method;
            var actual = production.RewriteMetadata(context, original);
            var expected = isolation.Rewrite(endpoint.AbsolutePath, original);
            if (actual is null || expected is null) throw new Exception("Expected both virtual ISO metadata transformations.");
            var actualJson = JsonNode.Parse(actual);
            var expectedJson = JsonNode.Parse(expected);
            if (!JsonNode.DeepEquals(actualJson, expectedJson))
            {
                var differences = Differences(expectedJson, actualJson, "$", []).Take(20);
                throw new Exception("Metadata differs between production and phone-tested isolation at: " + string.Join(", ", differences));
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                method,
                route = endpoint.AbsolutePath.Replace(user!, "{user}"),
                semanticEqual = true,
                byteEqual = actual.SequenceEqual(expected),
                bytes = actual.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(actual))
            }));
        }
    }

    private static List<string> Differences(JsonNode? expected, JsonNode? actual, string path, List<string> results)
    {
        if (JsonNode.DeepEquals(expected, actual)) return results;
        if (expected is JsonObject left && actual is JsonObject right)
            foreach (var name in left.Select(p => p.Key).Union(right.Select(p => p.Key))) Differences(left[name], right[name], path + "." + name, results);
        else if (expected is JsonArray a && actual is JsonArray b && a.Count == b.Count)
            for (var i = 0; i < a.Count; i++) Differences(a[i], b[i], path + "[" + i + "]", results);
        else results.Add(path);
        return results;
    }
}
