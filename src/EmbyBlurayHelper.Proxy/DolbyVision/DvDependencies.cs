using System.Diagnostics;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

internal static class DvDependencies
{
    internal static async Task<string> Check(DvOptions options, CancellationToken token)
    {
        if (!File.Exists(options.Library)) throw new InvalidOperationException("未找到本机 libdovi，请在 tools 目录放入对应平台的原生库，或在高级设置中指定完整路径。");
        try { using var native = new DoviNative(options.Library); }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { throw new InvalidOperationException("libdovi 无法加载，请使用与当前操作系统、处理器和系统运行库兼容的 3.4 原生库：" + e.GetType().Name); }
        return await CheckRemux(options, token);
    }
    internal static async Task<string> CheckRemux(DvOptions options, CancellationToken token)
    {
        if (!File.Exists(options.Ffmpeg)) throw new InvalidOperationException("未找到封装用 FFmpeg，请检查 Emby 原版探测器路径或指定独立 FFmpeg 路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var info = EmbyToolProcess.CreateStartInfo(options.Ffmpeg); info.ArgumentList.Add("-version");
        using var process = Process.Start(info) ?? throw new IOException("无法启动 FFmpeg。");
        using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        // Only -version is run here. No media probing or conversion on page entry.
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); var text = await output; await errors;
        if (process.ExitCode != 0) throw new IOException("FFmpeg 版本检查失败：" + process.ExitCode);
        return text.Split('\n')[0].Trim();
    }
}
