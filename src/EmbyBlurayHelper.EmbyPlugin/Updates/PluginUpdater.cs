using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace EmbyBlurayHelper.EmbyPlugin.Updates;

public sealed class PluginUpdateStatus
{
    public string LoadedVersion { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string State { get; set; } = "not-checked";
    public string Message { get; set; } = string.Empty;
    public string CheckedAt { get; set; } = string.Empty;
    public bool RestartRequired { get; set; }
    public bool CanInstall { get; set; }
}

// No Emby dependencies: validation and atomic file replacement are tested on both OSes.
public sealed class PluginUpdater
{
    private readonly string target;
    private readonly Version loaded;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object snapshotGate = new();
    private PluginUpdateManifest? candidate;
    private string candidateSource = string.Empty, checkedAt = string.Empty;

    public PluginUpdater(string pluginsPath, Version loadedVersion)
    {
        target = Path.Combine(Path.GetFullPath(pluginsPath), PluginUpdateManifest.FileName);
        loaded = VersionOf(loadedVersion.ToString());
    }

    public static string Source(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("请填写 Helper 的 HTTP/HTTPS 根地址，例如 http://127.0.0.1:8099，不要填写账号、密钥或下载文件路径。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private static Version VersionOf(string value)
    {
        if (!Version.TryParse(value, out var version)) throw new InvalidOperationException("插件版本号无效。");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    public PluginUpdateStatus Status(string sourceUrl)
    {
        var source = Source(sourceUrl);
        var installed = File.Exists(target) ? AssemblyName.GetAssemblyName(target).Version : null;
        var pending = installed is not null && VersionOf(installed.ToString()) > loaded;
        lock (snapshotGate)
        {
            var known = candidateSource == source ? candidate : null;
            var newer = known is not null && VersionOf(known.Version) > loaded;
            return new PluginUpdateStatus
            {
                LoadedVersion = loaded.ToString(3), InstalledVersion = installed?.ToString(3) ?? "未找到",
                AvailableVersion = known?.Version ?? string.Empty, Sha256 = known?.Sha256 ?? string.Empty,
                SourceUrl = source.TrimEnd('/'), CheckedAt = known is null ? string.Empty : checkedAt,
                RestartRequired = pending, CanInstall = !pending && newer && installed is not null,
                State = pending ? "pending-restart" : known is null ? "not-checked" : newer ? "update-available" : "up-to-date",
                Message = pending ? "新版 DLL 已安装，重启 Emby Server 后生效。当前仍运行旧版。" :
                    known is null ? "点击「检查更新」，读取此 Helper 随附的插件版本。" :
                    newer ? "Helper 提供新版插件，可以更新。" : "当前已加载版本不低于 Helper 提供版本，无需更新。"
            };
        }
    }

    private static async Task<byte[]> Read(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximum) throw new InvalidOperationException("更新响应超过允许大小，已停止。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<PluginUpdateManifest> Manifest(string source, Func<Uri, CancellationToken, Task<Stream>> fetch, CancellationToken token)
    {
        using var stream = await fetch(new Uri(new Uri(source), PluginUpdateManifest.ManifestRoute), token).ConfigureAwait(false);
        var bytes = await Read(stream, 16384, token).ConfigureAwait(false);
        using var json = new MemoryStream(bytes);
        var manifest = (PluginUpdateManifest?)new DataContractJsonSerializer(typeof(PluginUpdateManifest)).ReadObject(json)
            ?? throw new InvalidOperationException("Helper 未返回插件更新清单。");
        if (manifest.SchemaVersion != 1 || manifest.AssemblyName != PluginUpdateManifest.AssemblyIdentity ||
            manifest.Size < 1024 || manifest.Size > PluginUpdateManifest.MaximumBytes ||
            manifest.Sha256 is null || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Helper 的插件更新清单不符合要求，未安装任何文件。");
        VersionOf(manifest.Version);
        return manifest;
    }

    public async Task<PluginUpdateStatus> Check(string sourceUrl, Func<Uri, CancellationToken, Task<Stream>> fetch, CancellationToken token)
    {
        var source = Source(sourceUrl);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var manifest = await Manifest(source, fetch, token).ConfigureAwait(false);
            lock (snapshotGate)
            {
                candidate = manifest; candidateSource = source; checkedAt = DateTime.UtcNow.ToString("O");
            }
            return Status(source);
        }
        catch
        {
            lock (snapshotGate) { candidate = null; candidateSource = string.Empty; }
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<PluginUpdateStatus> Install(string sourceUrl, string expectedVersion, string expectedSha256,
        Func<Uri, CancellationToken, Task<Stream>> fetch, Action notifyRestart, CancellationToken token)
    {
        var source = Source(sourceUrl);
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var state = Status(source);
            if (state.RestartRequired) throw new InvalidOperationException("新版已安装，请先重启 Emby，避免重复更新。");
            if (!state.CanInstall || state.AvailableVersion != expectedVersion || !state.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先检查更新，再安装当前页面确认的版本。");
            var manifest = await Manifest(source, fetch, token).ConfigureAwait(false);
            if (manifest.Version != expectedVersion || !manifest.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Helper 提供的版本已变化，请重新检查更新。");
            using var stream = await fetch(new Uri(new Uri(source), PluginUpdateManifest.DownloadRoute), token).ConfigureAwait(false);
            var binary = await Read(stream, PluginUpdateManifest.MaximumBytes, token).ConfigureAwait(false);
            using var sha = SHA256.Create();
            var digest = BitConverter.ToString(sha.ComputeHash(binary)).Replace("-", "");
            if (binary.LongLength != manifest.Size || !digest.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DLL 大小或 SHA256 校验失败，原插件保持不变。");
            temporary = target + ".download-" + Guid.NewGuid().ToString("N");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await file.WriteAsync(binary, 0, binary.Length, token).ConfigureAwait(false); file.Flush(true); }
            var identity = AssemblyName.GetAssemblyName(temporary);
            if (identity.Name != PluginUpdateManifest.AssemblyIdentity || identity.Version is null ||
                VersionOf(identity.Version.ToString()) != VersionOf(manifest.Version))
                throw new InvalidOperationException("下载文件的程序集名称或版本不匹配，原插件保持不变。");
            token.ThrowIfCancellationRequested();
            // Same directory and volume. Loaded Windows DLLs remain mapped to the old image.
            // If the OS refuses replacement, fail safely rather than truncating the live DLL.
            File.Replace(temporary, target, target + ".previous", true);
            temporary = null;
            notifyRestart();
            return Status(source);
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}
