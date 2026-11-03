using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace EmbyBlurayHelper.EmbyPlugin.Media;

internal sealed class BluRayProbeTarget
{
    public BaseItem Item { get; set; } = null!;
    public long OwnerId { get; set; }
    public MediaSourceInfo Source { get; set; } = null!;
}

internal static class BluRaySourceResolver
{
    public static BluRayProbeTarget Resolve(ILibraryManager library, string itemId, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("ItemId is required.");
        var internalId = library.GetInternalId(itemId);
        var item = internalId > 0 ? library.GetItemById(internalId) : null;
        if (item is null) throw new ArgumentException("The Emby item does not exist.");
        var sources = item.GetMediaSources(true, false, library.GetLibraryOptions(item));
        var source = string.IsNullOrWhiteSpace(sourceId) ? sources.FirstOrDefault()
            : sources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null) throw new ArgumentException("The requested Emby media source was not found.");
        if (!IsBluRaySource(source.Container, source.Path))
            throw new ArgumentException("Only Blu-ray ISO and BDMV media sources are supported.");
        // Each version has its own owner; never apply another version's runtime to the parent.
        var ownerId = library.GetInternalId(itemId, source.Id);
        var owner = library.GetItemById(ownerId);
        if (owner is null || FindCurrent(library, owner, source) is null)
            throw new InvalidOperationException("The source owner could not be verified; no media information was changed.");
        return new BluRayProbeTarget { Item = item, OwnerId = ownerId, Source = source };
    }

    public static MediaSourceInfo? FindCurrent(ILibraryManager library, BaseItem owner, MediaSourceInfo source) =>
        owner.GetMediaSources(false, false, library.GetLibraryOptions(owner)).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, source.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Path, source.Path, StringComparison.Ordinal));

    internal static bool IsBluRaySource(string? container, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var extension = Path.GetExtension(normalized).ToLowerInvariant();
        if (extension is ".mkv" or ".mp4" or ".m2ts" or ".ts" or ".avi" or ".mov" or ".wmv" or ".strm" or ".m4v" or ".webm") return false;
        return extension == ".iso" || container?.Equals("bluray", StringComparison.OrdinalIgnoreCase) == true ||
            normalized.EndsWith("/BDMV", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/BDMV/index.bdmv", StringComparison.OrdinalIgnoreCase);
    }
}
