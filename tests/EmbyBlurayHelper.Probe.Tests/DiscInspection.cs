using BDInfo;
using DiscUtils.Udf;
using EmbyBlurayHelper.ProbeWrapper;
using System.Text.Json;

internal static class DiscInspection
{
    internal static int Run(string[] paths)
    {
        foreach (var path in paths)
        {
            using var image = File.Exists(path) ? File.OpenRead(path) : null;
            using var udf = image is null ? null : new UdfReader(image);
            using var metadata = udf is null ? null : IsoDiscMetadata.Open(udf, default);
            var disc = metadata?.Disc ?? new BDROM(new BDInfo.IO.DirectoryInfo(path));
            BluRayLanguageAugmenter.EnsureDiscIndexes(disc);
            var errors = new List<string>();
            foreach (var clip in disc.StreamClipFiles.Values)
                try { clip.Scan(); } catch (Exception e) { errors.Add(clip.Name + ": " + e.Message); }
            var playlists = new List<object>();
            foreach (var playlist in disc.PlaylistFiles.Values)
            {
                try
                {
                    playlist.Scan(disc.StreamFiles, disc.StreamClipFiles);
                    var clips = playlist.StreamClips.Where(c => c.AngleIndex == 0).ToArray();
                    playlists.Add(new {
                        playlist.Name, Duration = playlist.TotalLength, playlist.HasLoops,
                        Repeated = IsoDiscMetadata.IsRepetitive(clips.Select(c => (c.Name, c.TimeIn, c.TimeOut))),
                        ClipCount = clips.Length,
                        DistinctClips = clips.Select(c => c.Name).Distinct().Count(),
                        Clips = clips.Take(6).Select(c => new { c.Name, c.TimeIn, c.TimeOut, c.Length }).ToArray(),
                        Streams = playlist.PlaylistStreams.Values.Select(s => new {
                            Pid = $"0x{s.PID:X4}", Type = s.StreamType.ToString(), Language = s.LanguageCode }).ToArray()
                    });
                }
                catch (Exception e) { errors.Add(playlist.Name + ": " + e.Message); }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Path = path, Errors = errors,
                Playlists = playlists.OrderByDescending(p => (double)p.GetType().GetProperty("Duration")!.GetValue(p)!).Take(12)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }
}
