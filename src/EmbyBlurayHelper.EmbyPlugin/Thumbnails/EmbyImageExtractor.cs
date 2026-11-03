using System.Reflection;
using System.Runtime.ExceptionServices;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

// Reuse Emby's public native implementation for configured ffmpeg, environment,
// thread limit and HDR processing. Bind version differences here, without
// modifying private members or replacing the server's ffmpeg.
internal sealed class EmbyImageExtractor(IServerApplicationHost host)
{
    internal string ColorProcessing { get; private set; } = "SDR";
    internal async Task<string> One(MediaSourceInfo source, string path, MediaStream video, MediaProtocol protocol,
        Video3DFormat? format, TimeSpan time, string output, CancellationToken token)
    {
        var type = Assembly.Load("Emby.Server.MediaEncoding").GetType("Emby.Server.MediaEncoding.ImageExtraction.SingleImageExtractor")
            ?? throw new NotSupportedException("此 Emby 版本未提供兼容的单帧截图引擎。");
        var instance = NativeKeyframeExtractorFactory.Create(type, host);
        try
        {
            var hdr = DiscThumbnailColorPolicy.NeedsToneMapping(video);
            if (hdr)
            {
                // Dry-run Emby's protected filter builder. This uses the actual
                // server capabilities and diagnostic settings, without changing
                // them. Older implementations that only boost saturation are
                // intentionally refused, as are unsupported HLG/P5 sources.
                var filters = new List<string>();
                var builder = type.GetMethod("AddHdrAdjustFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new NotSupportedException("此 Emby 版本无法核验 HDR 截图色调映射。");
                builder.Invoke(instance, new object[] { filters, video });
                DiscThumbnailColorPolicy.ValidateFilters(hdr, filters);
                ColorProcessing = (video.ExtendedVideoType == ExtendedVideoTypes.DolbyVision ? "DV compatible base layer → SDR" : "HDR → SDR") +
                    " (" + string.Join(",", filters) + ")";
            }
            var method = type.GetMethods().SingleOrDefault(m => m.Name == "RunExtraction" && m.GetParameters().Length == 14)
                ?? throw new NotSupportedException("此 Emby 版本的单帧截图接口不兼容。");
            var prefix = method.GetParameters()[0].ParameterType == typeof(MediaSourceInfo)
                ? new object?[] { source, path } : new object?[] { path, new Dictionary<string, string>() };
            var arguments = prefix.Concat(new object?[] { MediaContainers.MpegTs, video, protocol, (int?)video.Index, format,
                (TimeSpan?)time, null, Path.GetDirectoryName(output), Path.GetFileName(output), (int?)320, false, token }).ToArray();
            await ((Task)method.Invoke(instance, arguments)!).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("Emby 未生成章节截图。");
            return output;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        finally { (instance as IDisposable)?.Dispose(); }
    }
}
