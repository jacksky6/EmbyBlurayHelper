using System.Diagnostics;
using EmbyBlurayHelper.Proxy.Chapters;

// A player that does not open the image as a disc falls back to libavformat's
// container probe. A real Blu-ray ISO is rejected there; the virtual one must be too.
internal static class GenericProbeCheck
{
    internal static async Task Run(DiscChapterPresentation presentation,string probe)
    {
        var directory=Path.Combine(Path.GetTempPath(),"emby-virtual-disc-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"head.iso");
        try
        {
            var head=new byte[VirtualUdfImage.MinimumMediaOffset+16*1024*1024];
            using(var input=presentation.Image.Open())input.ReadExactly(head);
            await File.WriteAllBytesAsync(path,head);
            foreach(var (size,expected) in new[]{(0L,""),(VirtualUdfImage.MinimumMediaOffset,""),(head.LongLength,"mpegts")})
            {
                var info=new ProcessStartInfo(probe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var a in new[]{"-v","error"}.Concat(size>0?new[]{"-formatprobesize",size.ToString()}:[]).Concat(["-show_entries","format=format_name","-of","default=nk=1:nw=1",path]))info.ArgumentList.Add(a);
                using var ffprobe=Process.Start(info)!;var stdout=ffprobe.StandardOutput.ReadToEndAsync();var stderr=ffprobe.StandardError.ReadToEndAsync();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
                try{await ffprobe.WaitForExitAsync(timeout.Token);}catch{ffprobe.Kill(true);throw;}
                var format=(await stdout).Trim();var errors=(await stderr).Trim();
                var window=size>0?size+" byte":"default";
                // Some ffprobe builds exit 0 after a failed open; judge by the reported format.
                if(expected.Length==0&&(format.Length>0||!errors.Contains("Invalid data found",StringComparison.Ordinal)))
                    throw new Exception($"Generic probe with {window} window opened the virtual ISO (exit {ffprobe.ExitCode}, format '{format}'): {errors[..Math.Min(errors.Length,400)]}");
                if(expected.Length>0&&format!=expected)throw new Exception($"Control probe past the gap did not find {expected}; check is inconclusive: {errors[..Math.Min(errors.Length,400)]}");
                Console.WriteLine($"PASS: generic probe, {window} window: {(expected.Length==0?"rejected as invalid data":"control found "+format)}.");
            }
        }
        finally{if(File.Exists(path))File.Delete(path);Directory.Delete(directory);}
    }
}
