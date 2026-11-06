namespace EmbyBlurayHelper;

// Shared by the Helper probe launcher and Emby providers. Respect path boundaries;
// a library named Movies must not exclude Movies2. Windows/UNC is case insensitive,
// Unix paths remain case sensitive even when inspected from a Windows Helper.
internal static class LibraryPathScope
{
    internal static bool Contains(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        if (path!.StartsWith("concat:", StringComparison.OrdinalIgnoreCase))
            return path.Substring(7).Split('|').Any(part => Contains(root, part));
        static string Normalize(string value)
        {
            value = value.Trim().Trim('"');
            if (value.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.LocalPath;
            value = value.Replace('\\', '/');
            // The probe's input normalizer removes "file:" before this check.
            // Recover file:/// Unix and file:///C:/ Windows paths as well.
            if (value.StartsWith("///", StringComparison.Ordinal))
            {
                value = value.TrimStart('/');
                if (value.Length < 2 || value[1] != ':') value = "/" + value;
            }
            return value.TrimEnd('/');
        }
        root = Normalize(root); path = Normalize(path!);
        var comparison = root.StartsWith("//", StringComparison.Ordinal) || (root.Length > 1 && root[1] == ':')
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + "/", comparison);
    }
    internal static bool Allowed(IEnumerable<string> excludedRoots, string? path) => !excludedRoots.Any(root => Contains(root, path));
}
