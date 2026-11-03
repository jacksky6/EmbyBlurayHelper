using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

internal static class MediaFeatureEnricher
{
    internal static async Task<byte[]> Enrich(byte[] original, string input, string identity, MediaFeatureOptions? options, CancellationToken token)
    {
        if (options?.Enabled != true) return original;
        try
        {
            if (string.IsNullOrWhiteSpace(options.ProbePath) || !Path.IsPathFullyQualified(options.ProbePath) || !File.Exists(options.ProbePath) ||
                string.Equals(Path.GetFullPath(options.ProbePath), Environment.ProcessPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("supplementary ffprobe is not configured or accessible");
            var engine = new FileInfo(options.ProbePath);
            // Direct M2TS probes also need to invalidate cached evidence if the
            // file is replaced at the same path. Disc adapters add their own stamp.
            if (File.Exists(input))
            {
                var source = new FileInfo(input);
                identity += "|" + source.Length + "|" + source.LastWriteTimeUtc.Ticks;
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v1|" + identity + "|" +
                engine.FullName + "|" + engine.Length + "|" + engine.LastWriteTimeUtc.Ticks + "|" +
                JsonNode.Parse(original)?["streams"]?.ToJsonString())));
            var cache = Path.Combine(options.CacheDirectory ?? Path.Combine(Path.GetTempPath(), "emby-bluray-helper-features", Environment.UserName), key + ".json");
            JsonArray? evidence = null;
            if (File.Exists(cache))
            {
                try { evidence = JsonNode.Parse(await File.ReadAllTextAsync(cache, token))?.AsArray(); }
                catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { }
            }
            var cached = evidence is not null;
            if (evidence is null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var basic = await Read(options.ProbePath, ["-v", "error", "-probesize", "20000000", "-analyzeduration", "5000000",
                    "-f", "mpegts", "-i", input, "-show_streams", "-of", "json"], timeout.Token);
                JsonObject? frames = null;
                if (basic["streams"]?.AsArray().OfType<JsonObject>().Any(s => s["codec_name"]?.ToString() == "hevc" && MediaFeatureMerger.Pid(s) == 0x1015) == true)
                    frames = await Read(options.ProbePath, ["-v", "error", "-probesize", "20000000", "-analyzeduration", "5000000",
                        "-f", "mpegts", "-i", input, "-select_streams", "i:0x1015", "-read_intervals", "%+0.25", "-show_frames", "-of", "json"], timeout.Token);
                evidence = MediaFeatureMerger.ReadEvidence(basic, frames);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    var temporary = cache + "." + Guid.NewGuid().ToString("N");
                    await File.WriteAllTextAsync(temporary, evidence.ToJsonString(), token);
                    File.Move(temporary, cache, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var output = MediaFeatureMerger.Merge(original, evidence, out var description);
            options.Attempt?.Complete();
            await Console.Error.WriteLineAsync($"EmbyBlurayHelper.MediaFeatures: {(cached ? "cache" : "supplementary ffprobe")}: {description}. Original probe fields retained.");
            return output;
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            options.Attempt?.Fail(e.Message);
            await Console.Error.WriteLineAsync("EmbyBlurayHelper.MediaFeatures: enrichment skipped; original result retained (" + e.GetType().Name + ": " + e.Message + ").");
            return original;
        }
    }

    private static async Task<JsonObject> Read(string executable, string[] args, CancellationToken token)
    {
        var start = ProbeProcess.CreateStartInfo(executable);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start supplementary ffprobe.");
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(process.WaitForExitAsync(token), output, error);
        if (process.ExitCode != 0) throw new InvalidDataException("Supplementary ffprobe failed with exit " + process.ExitCode);
        return JsonNode.Parse(await output)?.AsObject() ?? throw new InvalidDataException("Supplementary ffprobe returned no JSON.");
    }
}
