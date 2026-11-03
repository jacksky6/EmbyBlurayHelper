using BDInfo;
using DiscUtils.Udf;
using System.Text.Json;
using EmbyBlurayHelper.Proxy.Chapters;

internal static class ChapterRoundTripCheck
{
    internal static void Run(UdfReader udf,JsonElement source,DiscChapterPresentation presentation)
    {
        var directory=Path.Combine(Path.GetTempPath(),"emby-mpls-roundtrip-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var path=Path.Combine(directory,presentation.Playlist);
            using(var input=udf.OpenFile(@"\BDMV\PLAYLIST\"+presentation.Playlist,FileMode.Open,FileAccess.Read))
            using(var output=File.Create(path))input.CopyTo(output);
            var root=source.GetProperty("Path").GetString()!;
            var disc=new BDROM(new BDInfo.IO.DirectoryInfo(root));
            var bdmv=Directory.Exists(Path.Combine(root,"BDMV"))?Path.Combine(root,"BDMV"):root;
            if(disc.StreamFiles.Count==0)foreach(var file in Directory.EnumerateFiles(Path.Combine(bdmv,"STREAM")))
                if(Path.GetExtension(file).Equals(".m2ts",StringComparison.OrdinalIgnoreCase))disc.StreamFiles.Add(Path.GetFileName(file).ToUpperInvariant(),new TSStreamFile(BDInfo.IO.FileInfo.FromFullName(file)));
            if(disc.StreamClipFiles.Count==0)foreach(var file in Directory.EnumerateFiles(Path.Combine(bdmv,"CLIPINF")))
                if(Path.GetExtension(file).Equals(".clpi",StringComparison.OrdinalIgnoreCase))disc.StreamClipFiles.Add(Path.GetFileName(file).ToUpperInvariant(),new TSStreamClipFile(BDInfo.IO.FileInfo.FromFullName(file)));
            var playlist=new TSPlaylistFile(disc,BDInfo.IO.FileInfo.FromFullName(path));playlist.Scan(disc.StreamFiles,disc.StreamClipFiles);
            var expected=source.GetProperty("Chapters").EnumerateArray().Where(c=>!c.TryGetProperty("MarkerType",out var marker)||marker.GetString() is null or "Chapter")
                .Select(c=>c.GetProperty("StartPositionTicks").GetInt64()/10000000d).ToArray();
            if(playlist.Chapters.Count!=expected.Length||playlist.Chapters.Zip(expected).Any(c=>Math.Abs(c.First-c.Second)>.00003))
                throw new Exception("Patched MPLS chapters differ from Emby after independent BDInfo parsing.");
            Console.WriteLine($"PASS: all {expected.Length} Emby chapter times round-trip through virtual UDF and BDInfo MPLS parsing.");
        }
        finally{Directory.Delete(directory,true);}
    }
}
