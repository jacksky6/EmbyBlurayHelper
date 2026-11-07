using System.Net.Http.Headers;

namespace EmbyBlurayHelper.Proxy;

// Presentation only: never rename a source or change the stable playback route.
// Parse both separators explicitly so Windows/UNC names also work on Linux/DSM.
internal sealed record VirtualMediaName(string FileName, string Path)
{
    internal static VirtualMediaName Chapters(string source) => Create(source, "Chapters", ".iso");
    internal static VirtualMediaName DolbyVision(string source) => Create(source, "P81", ".mkv");
    internal static VirtualMediaName MultiClip(string source) => Create(source, "MultiClip", ".m2ts");
    internal static VirtualMediaName Language(string source) => Create(source, "Language", ".m2ts");

    internal string ContentDisposition => new ContentDispositionHeaderValue("inline")
    {
        // ASCII fallback retains release-group text; filename* carries the
        // complete Unicode name without invalid raw UTF-8 HTTP header bytes.
        FileName = new string(FileName.Select(c => c is >= ' ' and <= '~' && c is not '"' and not '\\' ? c : '_').ToArray()),
        FileNameStar = FileName
    }.ToString();

    private static VirtualMediaName Create(string source, string feature, string extension)
    {
        var original = source.TrimEnd('/', '\\');
        var separator = original.Contains('\\') ? '\\' : '/';
        if (Leaf(original).Equals("index.bdmv", StringComparison.OrdinalIgnoreCase)) original = Parent(original);
        if (Leaf(original).Equals("BDMV", StringComparison.OrdinalIgnoreCase)) original = Parent(original);
        var name = Leaf(original);
        var folder = original;
        if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) { name = name[..^4]; folder = Parent(original); }
        name = new string(name.Select(c => char.IsControl(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "Bluray";
        var fileName = name + ".EmbyBlurayHelper." + feature + extension;
        return new(fileName, folder.Length == 0 ? fileName : folder.TrimEnd('/', '\\') + separator + fileName);
    }

    private static int Separator(string path) => Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
    private static string Leaf(string path) => path[(Separator(path) + 1)..];
    private static string Parent(string path)
    {
        var index = Separator(path);
        return index < 0 ? "" : path[..(index == 0 ? 1 : index)];
    }
}
