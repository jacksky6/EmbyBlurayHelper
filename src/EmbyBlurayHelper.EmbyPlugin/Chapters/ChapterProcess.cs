using System.Diagnostics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EmbyBlurayHelper.EmbyPlugin.Chapters;

[DataContract]
internal sealed class NativeChapterData
{
    [DataMember] public int Schema { get; set; }
    [DataMember] public string Path { get; set; } = string.Empty;
    [DataMember] public string SourceStamp { get; set; } = string.Empty;
    [DataMember] public string Playlist { get; set; } = string.Empty;
    [DataMember] public string MatchReason { get; set; } = string.Empty;
    [DataMember] public long DurationTicks { get; set; }
    [DataMember] public long[] Chapters { get; set; } = Array.Empty<long>();
}

internal static class ChapterProcess
{
    internal static string Validate(string reader)
    {
        if (!Path.IsPathFullyQualified(reader) || !File.Exists(reader) ||
            !(Path.GetFileName(reader).Equals("EmbyBlurayHelper.exe", StringComparison.OrdinalIgnoreCase) ||
              Path.GetFileName(reader) == "EmbyBlurayHelper" || Path.GetFileName(reader) == "probe.sh"))
            throw new ArgumentException("请填写与 Emby 同机且可执行的 Helper 路径（EmbyBlurayHelper 或 probe.sh）。");
        return reader;
    }
    internal static async Task<NativeChapterData> Read(string reader, string path, long runtime, long size, CancellationToken token)
    {
        var start = new ProcessStartInfo(Validate(reader)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--helper-chapters"); start.ArgumentList.Add(path);
        start.ArgumentList.Add(runtime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(95));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动原盘章节读取器。");
        using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error).ConfigureAwait(false);
        await Task.Run(() => process.WaitForExit(), CancellationToken.None).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("原盘导航读取失败：" + (await error.ConfigureAwait(false)).Trim());
        var json = await output.ConfigureAwait(false);
        if (json.Length > 2_000_000) throw new InvalidDataException("章节读取结果过大。");
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var data = (NativeChapterData?)new DataContractJsonSerializer(typeof(NativeChapterData)).ReadObject(bytes);
        if (data is null || data.Schema != 1 || data.Path != path || string.IsNullOrWhiteSpace(data.Playlist) || string.IsNullOrWhiteSpace(data.SourceStamp))
            throw new InvalidDataException("章节读取器返回的来源无效。");
        return data;
    }
}
