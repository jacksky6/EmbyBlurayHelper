using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace EmbyBlurayHelper.EmbyPlugin;

internal static class LibraryProcessingScope
{
    internal static bool Allowed(ILibraryManager library, BaseItem item, string? sourcePath = null)
    {
        var plugin = Plugin.Instance;
        var config = plugin.Configuration;
        if (config.ProcessingScopeServerId != plugin.ServerId || config.ExcludedLibraryIds.Length == 0) return true;
        var excluded = library.GetVirtualFolders().Where(f => config.ExcludedLibraryIds.Contains(f.Id.ToString(), StringComparer.Ordinal)).ToArray();
        for (var parent = item; parent is not null; parent = parent.Parent)
            if (config.ExcludedLibraryIds.Contains(parent.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture))) return false;
        return LibraryPathScope.Allowed(excluded.SelectMany(f => f.Locations ?? Array.Empty<string>()), sourcePath ?? item.Path);
    }
    internal static void RequireAllowed(ILibraryManager library, BaseItem item, string? path = null)
    {
        if (!Allowed(library, item, path)) throw new InvalidOperationException("该影片所属媒体库已在全局设置中排除，未执行补全。");
    }
}
