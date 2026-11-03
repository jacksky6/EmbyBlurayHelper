using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

// JPEG previews are SDR. Never silently save PQ/HLG or Dolby IPT pixels as SDR.
internal static class DiscThumbnailColorPolicy
{
    internal static bool NeedsToneMapping(MediaStream video)
    {
        var transfer = video.ColorTransfer ?? "";
        var dv = video.ExtendedVideoType == ExtendedVideoTypes.DolbyVision;
        if (dv && video.ExtendedVideoSubType.ToString() is not ("DoviProfile76" or "DoviProfile81" or "DoviProfile84"))
            throw new NotSupportedException("此杜比视界格式未确认 HDR10 / HLG 兼容底层，暂不生成截图，避免紫绿偏色。");
        var hdr = dv || transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
            transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
            video.ExtendedVideoType.ToString().StartsWith("Hdr", StringComparison.OrdinalIgnoreCase) ||
            video.ExtendedVideoType == ExtendedVideoTypes.HyperLogGamma;
        if (hdr && !transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) && !transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("HDR 传递函数信息缺失或不受支持，暂不生成截图；请先补全原盘媒体信息。");
        return hdr;
    }

    internal static void ValidateFilters(bool hdr, IEnumerable<string> filters)
    {
        if (!hdr) return;
        var valid = filters.Any(f => (f.StartsWith("supertonemap", StringComparison.Ordinal) ||
            f.StartsWith("tonemap=", StringComparison.Ordinal) || f.StartsWith("libplacebo=", StringComparison.Ordinal)) &&
            !System.Text.RegularExpressions.Regex.IsMatch(f, @"(?:^|[:=])tonemap=(?:none|0)(?:[:]|$)|^tonemap=(?:none|0)(?:[:]|$)"));
        if (!valid) throw new NotSupportedException("Emby 当前截图引擎未启用适用的 HDR → SDR 色调映射（滤镜缺失、被禁用或该传递函数不受支持）。保留已有图片，跳过本片，避免偏色。");
    }
}
