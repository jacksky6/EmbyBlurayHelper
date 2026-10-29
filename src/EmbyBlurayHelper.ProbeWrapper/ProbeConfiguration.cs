using System.Text.Json;

namespace EmbyBlurayHelper.ProbeWrapper;

internal sealed class ProbeConfiguration
{
    private const string ConfigFileName = "emby-bluray-helper.json";
    private const string UpstreamEnvironmentVariable = "EMBY_BLURAY_HELPER_UPSTREAM_PROBE";

    public string? UpstreamProbePath { get; init; }

    public static async Task<string> ResolveUpstreamPathAsync(CancellationToken cancellationToken)
    {
        var environmentPath = Environment.GetEnvironmentVariable(UpstreamEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return ValidatePath(environmentPath);
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (File.Exists(configPath))
        {
            await using var stream = File.OpenRead(configPath);
            var configuration = await JsonSerializer.DeserializeAsync<ProbeConfiguration>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(configuration?.UpstreamProbePath))
            {
                return ValidatePath(configuration.UpstreamProbePath);
            }
        }

        foreach (var candidate in GetPlatformDefaults())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not find Emby's original ffprobe. Set {UpstreamEnvironmentVariable} " +
            $"or configure 'upstreamProbePath' in {configPath}.");
    }

    private static string ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Configured upstream ffprobe does not exist: {fullPath}");
        }

        var currentExecutable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExecutable) &&
            string.Equals(
                Path.GetFullPath(currentExecutable),
                fullPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The upstream ffprobe path points back to this wrapper.");
        }

        return fullPath;
    }

    private static IEnumerable<string> GetPlatformDefaults()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield return Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
            yield return Path.Combine(programData, "Emby-Server", "system", "ffprobe.exe");
            yield break;
        }

        yield return "/var/packages/EmbyServer/target/bin/ffprobe";
        yield return "/usr/lib/emby-server/bin/ffprobe";
        yield return "/usr/bin/ffprobe";
        yield return "/bin/ffprobe";
    }
}
