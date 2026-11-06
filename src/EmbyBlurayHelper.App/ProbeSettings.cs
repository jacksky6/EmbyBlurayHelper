using System.Text.Json;

namespace EmbyBlurayHelper.App;

// No credentials are shared with the native DSM Emby account.
internal sealed record ProbeSettings(string UpstreamProbePath, bool EnableProbeLanguagePatch,
    bool EnableMediaFeatures = false, string FeatureProbePath = "", string[]? ExcludedLibraryPaths = null)
{
    public static ProbeSettings From(HelperSettings settings) => new(settings.UpstreamProbePath, settings.EnableProbeLanguagePatch,
        settings.EnableMediaFeatures, settings.FeatureProbePath, SettingsStore.ExcludedPaths(settings));
    public static string SharedPath => Path.Combine(AppContext.BaseDirectory, "probe-settings.json");
    public static string ExecutablePath => OperatingSystem.IsLinux() && File.Exists(SharedPath) && File.Exists(Path.Combine(AppContext.BaseDirectory, "probe.sh"))
        ? Path.Combine(AppContext.BaseDirectory, "probe.sh") : Environment.ProcessPath!;

    public static bool IsOurPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            return string.Equals(CanonicalPath(path), CanonicalPath(ExecutablePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static string CanonicalPath(string path)
    {
        // DSM's /var/packages/<name>/target is a directory symlink. Resolve parents too.
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null) current = entry.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return current;
    }

    public static ProbeSettings ReadShared() =>
        JsonSerializer.Deserialize<ProbeSettings>(File.ReadAllText(SharedPath), SettingsStore.Json)
            ?? throw new InvalidDataException("探测器共享配置为空。");

    public static void Export(HelperSettings settings)
    {
        var path = Environment.GetEnvironmentVariable("EMBY_BLURAY_HELPER_PROBE_CONFIG");
        if (string.IsNullOrEmpty(path)) return;
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(From(settings), SettingsStore.Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, path, true);
    }
}
