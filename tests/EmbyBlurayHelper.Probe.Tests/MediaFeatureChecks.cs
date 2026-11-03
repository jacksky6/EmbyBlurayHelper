using System.Text;
using System.Text.Json.Nodes;
using EmbyBlurayHelper.ProbeWrapper;
using EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

internal static class MediaFeatureChecks
{
    internal static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); count++; }
        var raw = """{"streams":[{"index":0,"id":"0x1011","codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"color_transfer":"smpte2084","side_data_list":[{"side_data_type":"Mastering display metadata","max_luminance":"1000/1"}]},{"index":1,"id":"0x1015","codec_type":"video","codec_name":"hevc","width":1920,"height":1080},{"index":2,"id":"0x1100","codec_type":"audio","codec_name":"truehd","channels":8,"tags":{"language":"zho","title":"自定义备注"}},{"index":3,"id":"0x1100","codec_type":"audio","codec_name":"ac3","channels":6}],"format":{"duration":"1234.56","emby_custom_field":"preserve"}}""";
        var baseline = Encoding.UTF8.GetBytes(raw);
        var modern = JsonNode.Parse(raw)!.AsObject();
        modern["streams"]![2]!["profile"] = "Dolby TrueHD + Dolby Atmos";
        modern["streams"]![2]!["channels"] = 16; // Supplementary technical fields must never replace Emby's.
        var evidence = MediaFeatureMerger.ReadEvidence(modern, null);
        Check(evidence.Count == 1, "two HEVC streams alone must not be classified as DV");
        var merged = JsonNode.Parse(MediaFeatureMerger.Merge(baseline, evidence, out _))!;
        Check(merged["streams"]![2]!["profile"]!.ToString().Contains("Atmos"), "Atmos is added only to confirmed TrueHD");
        Check(merged["streams"]![3]!["profile"] is null, "same-PID AC3 core must not acquire Atmos");
        Check(merged["streams"]![2]!["channels"]!.GetValue<int>() == 8, "original channel count is preserved");
        Check(merged["streams"]![2]!["tags"]!["title"]!.ToString() == "自定义备注", "user title is preserved");
        Check(merged["streams"]![2]!["tags"]!["language"]!.ToString() == "zho", "existing language is preserved");
        Check(merged["format"]!.ToJsonString() == JsonNode.Parse(raw)!["format"]!.ToJsonString(), "all original format fields are preserved");
        var frames = JsonNode.Parse("""{"frames":[{"stream_index":1,"side_data_list":[{"side_data_type":"Dolby Vision Metadata","rpu_type":2,"vdr_rpu_profile":1,"bl_bit_depth":10,"el_bit_depth":10,"disable_residual_flag":0}]}]}""")!.AsObject();
        evidence = MediaFeatureMerger.ReadEvidence(modern, frames);
        var dv = JsonNode.Parse(MediaFeatureMerger.Merge(baseline, evidence, out _))!;
        Check(dv["streams"]![0]!["side_data_list"]!.AsArray().Count == 2, "DOVI information supplements original HDR mastering metadata");
        Check(dv["streams"]![0]!["side_data_list"]![1]!["dv_profile"]!.GetValue<int>() == 7, "validated BL/EL/RPU identifies profile 7");
        Check(dv["streams"]![0]!["tags"]!["title"]!.ToString().Contains("FEL"), "residual-enabled RPU identifies FEL");
        Check(dv["streams"]![1]!["side_data_list"] is null, "enhancement stream is not mislabeled as the primary DV video");
        frames["frames"]![0]!["stream_index"] = 0;
        Check(MediaFeatureMerger.ReadEvidence(modern, frames).Count == 1, "RPU from a different stream cannot label the enhancement PID");
        modern["streams"]![2]!.AsObject().Remove("profile");
        Check(MediaFeatureMerger.ReadEvidence(modern, null).Count == 0, "TrueHD 7.1 alone is not Atmos evidence");
        Check(ReferenceEquals(MediaFeatureMerger.Merge(baseline, new JsonArray(), out _), baseline), "no evidence passes the exact original bytes through");
        Check(ReferenceEquals(await MediaFeatureEnricher.Enrich(baseline, "unused", "unused", new MediaFeatureOptions(false, "missing"), default), baseline),
            "disabled supplementary probe never changes or parses the original result");
        Check(ReferenceEquals(await MediaFeatureEnricher.Enrich(baseline, "unused", "unused", new MediaFeatureOptions(true, "missing"), default), baseline),
            "an unavailable supplementary engine retains the exact successful original result");
        Check(!ProbeProcess.IsBdmvStream("/media/loose.m2ts") && !ProbeProcess.IsBdmvStream("/media/BDMV/STREAM/movie.mkv"),
            "ordinary files cannot invoke supplementary probing through their extension or a BDMV parent name");
        Check(ProbeProcess.IsBdmvStream("/media/movie/BDMV/STREAM/00001.m2ts"), "original BDMV clip probes remain supported");
        var receiptSource = Path.Combine(Path.GetTempPath(), "feature-receipt-test-" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllText(receiptSource, "bounded fixture");
        try
        {
            var started = DateTime.UtcNow;
            MediaFeatureReceipt.Write(receiptSource, true, "completed with no additional evidence");
            Check(MediaFeatureReceipt.Confirm(receiptSource, started, out _), "a fresh successful supplementary receipt confirms negative samples too");
            Check(!MediaFeatureReceipt.Confirm(receiptSource, DateTime.UtcNow.AddSeconds(1), out _), "an older receipt cannot confirm another probe request");
            MediaFeatureReceipt.Write(receiptSource, false, "timed out");
            Check(!MediaFeatureReceipt.Confirm(receiptSource, started, out var reason) && reason == "timed out", "supplementary failure cannot be reported as a successful negative result");
            MediaFeatureReceipt.Write(receiptSource, true, "completed");
            File.AppendAllText(receiptSource, "replacement");
            Check(!MediaFeatureReceipt.Confirm(receiptSource, started, out _), "replaced media invalidates its receipt");
        }
        finally { File.Delete(receiptSource); }
        Console.WriteLine($"MediaFeatures: {count} protection and identification checks passed.");
        return count;
    }

    internal static async Task<int> ReadDisc(string[] args)
    {
        var path = args[2];
        var options = new MediaFeatureOptions(true, args[1]);
        var input = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "BDMV", "index.bdmv");
        var arguments = new[] { "-i", input, "-v", "error", "-of", "json", "-show_streams", "-show_format" };
        var result = input.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
            ? await IsoMediaProbe.Run(args[0], arguments, input, null, default, options)
            : await IsoMediaProbe.RunDirectory(args[0], arguments, input, default, options);
        var json = JsonNode.Parse(result.Output)!;
        if (args.Length > 3) await File.WriteAllBytesAsync(args[3], result.Output);
        foreach (var s in json["streams"]!.AsArray().OfType<JsonObject>().Where(s => s["codec_type"]?.ToString() is "video" or "audio"))
            Console.WriteLine($"#{s["index"]} PID {s["id"]} {s["codec_name"]} {s["profile"]} {s["tags"]?["title"]} DV={s["side_data_list"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => d["side_data_type"]?.ToString() == "DOVI configuration record")}");
        return 0;
    }

    internal static int MakeFixture(string source, string target)
    {
        target = Path.GetFullPath(target);
        if (!target.StartsWith(Path.GetFullPath("artifacts/checks") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(target))
            throw new ArgumentException("A new directory under artifacts/checks is required for the bounded media fixture.");
        using var metadata = IsoDiscMetadata.OpenDirectory(Path.Combine(source, "BDMV", "index.bdmv"), default);
        var playlist = metadata.SelectPlaylist(default);
        foreach (var name in new[] { "PLAYLIST", "CLIPINF", "STREAM" }) Directory.CreateDirectory(Path.Combine(target, "BDMV", name));
        File.Copy(Path.Combine(source, "BDMV", "index.bdmv"), Path.Combine(target, "BDMV", "index.bdmv"));
        File.Copy(Path.Combine(source, "BDMV", "PLAYLIST", playlist.Name), Path.Combine(target, "BDMV", "PLAYLIST", playlist.Name));
        foreach (var clip in playlist.StreamClips.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            File.Copy(Path.Combine(source, "BDMV", "CLIPINF", Path.ChangeExtension(clip, ".clpi")), Path.Combine(target, "BDMV", "CLIPINF", Path.ChangeExtension(clip, ".clpi")));
            using var input = File.OpenRead(metadata.StreamPaths[clip]);
            using var output = File.Create(Path.Combine(target, "BDMV", "STREAM", clip));
            var buffer = new byte[1024 * 1024];
            for (var remaining = Math.Min(input.Length, 64L * 1024 * 1024); remaining > 0;)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (read == 0) break;
                output.Write(buffer, 0, read); remaining -= read;
            }
        }
        Console.WriteLine("Created bounded BDMV scan fixture: " + target + ". This is a test sample, not a complete movie.");
        return 0;
    }
}
