using System.Diagnostics;
using System.Text.Json;
using EmbyBlurayHelper.Proxy.Chapters;

internal static class NativePreviewCheck
{
    internal static async Task Run(DiscChapterPresentation presentation,string probe)
    {
        var directory=Path.Combine(Path.GetTempPath(),"emby-virtual-disc-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"preview.iso");
        try
        {
            File.WriteAllBytes(path,[]);
            if(OperatingSystem.IsWindows())
            {
                var sparse=new ProcessStartInfo("fsutil.exe"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var a in new[]{"sparse","setflag",path})sparse.ArgumentList.Add(a);
                using var process=Process.Start(sparse)!;await process.WaitForExitAsync();
                if(process.ExitCode!=0)throw new Exception("Cannot mark preview sparse; refusing to allocate a movie-sized file.");
            }
            long copied=0;
            using(var output=new FileStream(path,FileMode.Open,FileAccess.Write))
            using(var input=presentation.Image.Open())
            {
                output.SetLength(input.Length);
                foreach(var region in presentation.Image.Regions)
                {
                    foreach(var range in region.Metadata?new[]{(region.Offset,region.Length)}:
                        new[]{(region.Offset,Math.Min(region.Length,4*1024*1024L)),(Math.Max(region.Offset,region.Offset+region.Length-4*1024*1024L),Math.Min(region.Length,4*1024*1024L))})
                    {
                        output.Position=range.Item1;input.Position=range.Item1;
                        var b=new byte[(int)range.Item2];input.ReadExactly(b);output.Write(b);copied+=b.Length;
                    }
                }
            }
            var info=new ProcessStartInfo(probe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var a in new[]{"-v","info","-probesize","1M","-analyzeduration","100000","-show_entries","stream=codec_name,width,height:format=duration","-of","json","bluray:"+path})info.ArgumentList.Add(a);
            using var ffprobe=Process.Start(info)!;var stdout=ffprobe.StandardOutput.ReadToEndAsync();var stderr=ffprobe.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try{await ffprobe.WaitForExitAsync(timeout.Token);}catch{ffprobe.Kill(true);throw;}
            var text=await stdout;var errors=await stderr;
            if(ffprobe.ExitCode!=0)throw new Exception("Native Blu-ray probe failed: "+errors[..Math.Min(errors.Length,1200)]);
            using var json=JsonDocument.Parse(text);var streams=json.RootElement.GetProperty("streams");
            if(!streams.EnumerateArray().Any(s=>s.TryGetProperty("width",out var w)&&w.GetInt32()>=1920))throw new Exception("Native Blu-ray video stream not detected: "+text);
            if(!errors.Contains("selected "+presentation.Playlist,StringComparison.OrdinalIgnoreCase))throw new Exception("Native Blu-ray title selection differs: "+errors);
            Console.WriteLine(JsonSerializer.Serialize(new{NativeProbe=probe,Streams=streams,PreviewBytesCopied=copied,LogicalImageBytes=presentation.Image.Length}));
        }
        finally{if(File.Exists(path))File.Delete(path);Directory.Delete(directory);}
    }
}
