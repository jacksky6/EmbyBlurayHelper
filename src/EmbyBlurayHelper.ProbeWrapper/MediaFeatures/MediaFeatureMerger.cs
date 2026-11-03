using System.Globalization;
using System.Text.Json.Nodes;

namespace EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

internal static class MediaFeatureMerger
{
    internal static int? Pid(JsonNode? stream) => int.TryParse(stream?["id"]?.ToString()?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid) ? pid : null;

    internal static JsonArray ReadEvidence(JsonObject supplemental, JsonObject? frames)
    {
        var evidence = new JsonArray();
        var streams = supplemental["streams"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        foreach (var stream in streams)
        {
            if (Pid(stream) is not { } pid) continue;
            var profile = stream["profile"]?.ToString();
            if (stream["codec_type"]?.ToString() == "audio" &&
                stream["codec_name"]?.ToString() is "truehd" or "eac3" &&
                profile?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true)
                evidence.Add(new JsonObject { ["pid"] = pid, ["codec"] = stream["codec_name"]!.ToString(),
                    ["profile"] = profile, ["title"] = stream["codec_name"]!.ToString() == "truehd" ? "Dolby TrueHD Atmos" : "Dolby Digital Plus Atmos" });
            var dovi = stream["side_data_list"]?.AsArray().OfType<JsonObject>()
                .FirstOrDefault(s => s["side_data_type"]?.ToString() == "DOVI configuration record");
            if (stream["codec_type"]?.ToString() == "video" && dovi is not null)
                evidence.Add(new JsonObject { ["pid"] = pid, ["codec"] = stream["codec_name"]!.ToString(), ["dovi"] = dovi.DeepClone() });
        }

        // UHD Blu-ray Profile 7 stores RPU in a separate HEVC enhancement PID.
        // Require actual decoded RPU metadata plus the standard BL/EL pair.
        // A second HEVC stream or a filename alone is never evidence of DV.
        var bl = streams.SingleOrDefault(s => Pid(s) == 0x1011 && s["codec_name"]?.ToString() == "hevc" &&
            s["width"]?.GetValue<int>() == 3840 && s["height"]?.GetValue<int>() == 2160 && s["color_transfer"]?.ToString() == "smpte2084");
        var el = streams.SingleOrDefault(s => Pid(s) == 0x1015 && s["codec_name"]?.ToString() == "hevc" &&
            s["width"]?.GetValue<int>() == 1920 && s["height"]?.GetValue<int>() == 1080);
        if (bl is not null && el is not null && !evidence.OfType<JsonObject>().Any(e => e["pid"]?.GetValue<int>() == 0x1011 && e["dovi"] is not null))
        {
            var rpu = frames?["frames"]?.AsArray().OfType<JsonObject>()
                .Where(f => f["stream_index"]?.ToString() == el["index"]?.ToString())
                .SelectMany(f => f["side_data_list"]?.AsArray().OfType<JsonObject>() ?? [])
                .FirstOrDefault(s => s["side_data_type"]?.ToString() == "Dolby Vision Metadata" &&
                    s["rpu_type"]?.GetValue<int>() == 2 && s["vdr_rpu_profile"]?.GetValue<int>() == 1 &&
                    s["bl_bit_depth"]?.GetValue<int>() == 10 && s["el_bit_depth"]?.GetValue<int>() == 10 &&
                    s["disable_residual_flag"] is not null);
            if (rpu is not null)
            {
                var layer = rpu["disable_residual_flag"]!.GetValue<int>() == 0 ? "FEL" : "MEL";
                evidence.Add(new JsonObject { ["pid"] = 0x1011, ["codec"] = "hevc", ["title"] = "Dolby Vision Profile 7 " + layer + " / HDR10",
                    ["dovi"] = new JsonObject { ["side_data_type"] = "DOVI configuration record", ["dv_version_major"] = 1,
                        ["dv_version_minor"] = 0, ["dv_profile"] = 7, ["rpu_present_flag"] = 1,
                        ["el_present_flag"] = 1, ["bl_present_flag"] = 1, ["dv_bl_signal_compatibility_id"] = 6 } });
            }
        }
        return evidence;
    }

    internal static byte[] Merge(byte[] original, JsonArray evidence, out string description)
    {
        var result = JsonNode.Parse(original)!.AsObject();
        var descriptions = new List<string>();
        var changed = false;
        foreach (var feature in evidence.OfType<JsonObject>())
        {
            var matches = result["streams"]?.AsArray().OfType<JsonObject>().Where(s =>
                Pid(s) == feature["pid"]?.GetValue<int>() && s["codec_name"]?.ToString() == feature["codec"]?.ToString()).ToArray() ?? [];
            if (matches.Length != 1) continue; // Never match HD/core pairs by index alone.
            var stream = matches[0];
            if (feature["profile"] is not null && stream["codec_type"]?.ToString() == "audio")
            { stream["profile"] = feature["profile"]!.DeepClone(); changed = true; }
            if (feature["dovi"] is JsonObject dovi && stream["codec_type"]?.ToString() == "video")
            {
                var sideData = stream["side_data_list"] as JsonArray ?? new JsonArray();
                if (stream["side_data_list"] is null) stream["side_data_list"] = sideData;
                if (!sideData.OfType<JsonObject>().Any(s => s["side_data_type"]?.ToString() == "DOVI configuration record"))
                { sideData.Add(dovi.DeepClone()); changed = true; }
            }
            if (feature["title"] is not null && string.IsNullOrWhiteSpace(stream["tags"]?["title"]?.ToString()))
            {
                if (stream["tags"] is not JsonObject) stream["tags"] = new JsonObject();
                var title = feature["title"]!.ToString();
                if (stream["codec_type"]?.ToString() == "audio")
                {
                    var channels = stream["channel_layout"]?.ToString();
                    channels ??= stream["channels"]?.ToString() switch { "8" => "7.1", "6" => "5.1", "2" => "2.0", _ => null };
                    if (!string.IsNullOrWhiteSpace(channels)) title += " " + channels;
                }
                else if (stream["codec_type"]?.ToString() == "video" && stream["width"]?.GetValue<int>() >= 3800)
                    title = "4K HEVC · " + title;
                stream["tags"]!["title"] = title;
                changed = true;
            }
            descriptions.Add($"PID 0x{feature["pid"]!.GetValue<int>():X4} {feature["title"] ?? feature["profile"] ?? (JsonNode?)"Dolby Vision"}");
        }
        description = descriptions.Count == 0 ? "no additional Atmos/Dolby Vision confirmed in the sample" : string.Join("; ", descriptions);
        return changed ? System.Text.Encoding.UTF8.GetBytes(result.ToJsonString()) : original;
    }
}
