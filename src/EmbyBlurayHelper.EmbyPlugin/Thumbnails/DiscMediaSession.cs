using System.Diagnostics;
using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using EmbyBlurayHelper.EmbyPlugin.Chapters;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

[DataContract]
internal sealed class DiscMediaDescription
{
    [DataMember] public int Schema { get; set; }
    [DataMember] public string Path { get; set; } = "";
    [DataMember] public string SourceStamp { get; set; } = "";
    [DataMember] public string Playlist { get; set; } = "";
    [DataMember] public long DurationTicks { get; set; }
    [DataMember] public DiscMediaClip[] Clips { get; set; } = Array.Empty<DiscMediaClip>();
}
[DataContract]
internal sealed class DiscMediaClip
{
    [DataMember] public string Name { get; set; } = "";
    [DataMember] public string InputPath { get; set; } = "";
    [DataMember] public long TimelineStartTicks { get; set; }
    [DataMember] public long TimelineEndTicks { get; set; }
    [DataMember] public long OffsetTicks { get; set; }
}

internal sealed class DiscMediaSession : IDisposable
{
    private readonly Process process;
    private readonly CancellationTokenRegistration stop;
    private readonly Task<string> errors;
    internal DiscMediaDescription Data { get; private set; } = null!;
    private DiscMediaSession(Process process, CancellationToken token)
    {
        this.process = process;
        errors = process.StandardError.ReadToEndAsync();
        stop = token.Register(Kill);
    }
    internal static async Task<DiscMediaSession> Open(string reader, string path, long runtime, long size, CancellationToken token)
    {
        var start = new ProcessStartInfo(ChapterProcess.Validate(reader)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--helper-thumbnail-session", path, runtime.ToString(CultureInfo.InvariantCulture), size.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        var session = new DiscMediaSession(Process.Start(start) ?? throw new IOException("无法启动原盘截图读取器。"), token);
        try
        {
            var line = session.process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(95), token)).ConfigureAwait(false) != line)
            { token.ThrowIfCancellationRequested(); throw new TimeoutException("原盘截图导航读取超时。"); }
            var json = await line.ConfigureAwait(false);
            if (json is null) throw new IOException("截图读取器未返回导航：" + (await session.errors.ConfigureAwait(false)).Trim());
            if (json.Length > 2_000_000) throw new InvalidDataException("原盘截图导航结果过大。");
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(json));
            session.Data = (DiscMediaDescription)new DataContractJsonSerializer(typeof(DiscMediaDescription)).ReadObject(bytes)!;
            var data = session.Data;
            if (data.Schema != 1 || data.Path != path || data.DurationTicks <= 0 || string.IsNullOrEmpty(data.SourceStamp) ||
                data.Clips.Length == 0 || data.Clips.Any(c => c.TimelineEndTicks <= c.TimelineStartTicks || c.TimelineStartTicks < 0 ||
                    !(System.IO.Path.IsPathFullyQualified(c.InputPath) || Uri.TryCreate(c.InputPath, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidDataException("原盘截图导航无效。");
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    private void Kill() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
    public void Dispose()
    {
        stop.Dispose();
        try { if (!process.HasExited) { process.StandardInput.Close(); if (!process.WaitForExit(5000)) Kill(); } }
        catch (InvalidOperationException) { }
        process.Dispose();
    }
}
