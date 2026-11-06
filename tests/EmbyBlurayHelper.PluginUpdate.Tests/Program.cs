using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using EmbyBlurayHelper.EmbyPlugin.Updates;

[MethodImpl(MethodImplOptions.NoInlining)]
static (Version?, PluginUpdateStatus) WhileMapped(string path, Func<Task<PluginUpdateStatus>> install)
{
    var context = new AssemblyLoadContext("old-plugin-update-fixture", isCollectible: true);
    try
    {
        var assembly = context.LoadFromAssemblyPath(path);
        var result = install().GetAwaiter().GetResult();
        return (assembly.GetName().Version, result);
    }
    finally { context.Unload(); }
}

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); throw new Exception("Expected rejection: " + message); }
    catch (Exception e) when (e is InvalidOperationException or BadImageFormatException or OperationCanceledException or IOException) { checks++; }
}
foreach (var source in new[] { "file:///etc/passwd", "http://user:password@localhost:8099", "http://localhost:8099/?key=x", "http://localhost/#secret", "not a url" })
    await Reject(() => Task.Run(() => PluginUpdater.Source(source)), "invalid source " + source);
Check(PluginUpdater.Source(" http://127.0.0.1:8099 ") == "http://127.0.0.1:8099/", "default address normalized");
Check(PluginUpdater.Source("http://helper:8099/base/") == "http://helper:8099/base/", "container and reverse-proxy roots supported");
var root = Path.Combine(Path.GetTempPath(), "bluray-plugin-update-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var target = Path.Combine(root, PluginUpdateManifest.FileName);
var old = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "old-plugin.dll"));
var latest = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "update-plugin.dll"));
File.WriteAllBytes(target, old);
var updater = new PluginUpdater(root, new Version(0, 1, 6, 0));
var manifest = new PluginUpdateManifest { Version = "0.1.8", Size = latest.Length, Sha256 = Convert.ToHexString(SHA256.HashData(latest)) };
var payload = latest;
var requests = 0;
var sourceUrl = "http://127.0.0.1:8099";
Task<Stream> Fetch(Uri uri, CancellationToken token)
{
    token.ThrowIfCancellationRequested(); requests++;
    if (uri.AbsolutePath == "/" + PluginUpdateManifest.ManifestRoute)
        return Task.FromResult<Stream>(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    Check(uri.AbsolutePath == "/" + PluginUpdateManifest.DownloadRoute, "only the fixed DLL download path is used");
    return Task.FromResult<Stream>(new MemoryStream(payload));
}
Task Install() => updater.Install(sourceUrl, manifest.Version, manifest.Sha256, Fetch, () => { }, CancellationToken.None);
try
{
    Check(updater.Status(sourceUrl).State == "not-checked" && requests == 0, "status never polls Helper");
    await Reject(Install, "installation requires an explicit successful check");
    var status = await updater.Check(sourceUrl, Fetch, CancellationToken.None);
    Check(status.CanInstall && status.LoadedVersion == "0.1.6" && status.AvailableVersion == "0.1.8", "newer version can be installed");
    Check(!updater.Status("http://other:8099").CanInstall, "changing source invalidates the old check");
    payload = latest[..^1];
    await Reject(Install, "truncated downloads fail verification");
    Check(File.ReadAllBytes(target).SequenceEqual(old) && !File.Exists(target + ".previous"), "failed transfer leaves original and backup untouched");
    payload = (byte[])latest.Clone(); payload[^1] ^= 1;
    await Reject(Install, "same-length corrupt DLL fails SHA256");
    payload = (byte[])old.Clone();
    var name = System.Text.Encoding.UTF8.GetBytes(PluginUpdateManifest.AssemblyIdentity);
    for (var i = 0; i <= payload.Length - name.Length; i++)
        if (payload.AsSpan(i, name.Length).SequenceEqual(name)) payload[i] = (byte)'X';
    manifest.Size = payload.Length; manifest.Sha256 = Convert.ToHexString(SHA256.HashData(payload));
    await updater.Check(sourceUrl, Fetch, CancellationToken.None);
    await Reject(Install, "valid .NET assembly with wrong identity is rejected");
    Check(!Directory.GetFiles(root, "*.download-*").Any() && File.ReadAllBytes(target).SequenceEqual(old), "validation cleans temporary files");
    payload = old; manifest.Size = old.Length; manifest.Sha256 = Convert.ToHexString(SHA256.HashData(old));
    await updater.Check(sourceUrl, Fetch, CancellationToken.None);
    await Reject(Install, "DLL version must match manifest");
    payload = latest; manifest.Size = latest.Length; manifest.Sha256 = Convert.ToHexString(SHA256.HashData(latest));
    await updater.Check(sourceUrl, Fetch, CancellationToken.None);
    var before = manifest.Sha256; manifest.Sha256 = new string('0', 64);
    await Reject(() => updater.Install(sourceUrl, "0.1.8", before, Fetch, () => { }, CancellationToken.None), "source changing after check requires a recheck");
    manifest.Sha256 = before;
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        await Reject(() => updater.Install(sourceUrl, "0.1.8", before, Fetch, () => { }, cancelled.Token), "cancelled update does not write");
    }
    manifest.SchemaVersion = 9;
    await Reject(() => updater.Check(sourceUrl, Fetch, CancellationToken.None), "unknown manifest schema is rejected");
    Check(!updater.Status(sourceUrl).CanInstall, "failed check clears an earlier candidate");
    manifest.SchemaVersion = 1;
    manifest.Version = "0.1.5";
    Check(!(await updater.Check(sourceUrl, Fetch, CancellationToken.None)).CanInstall, "downgrades are never offered");
    manifest.Version = "0.1.6";
    Check(!(await updater.Check(sourceUrl, Fetch, CancellationToken.None)).CanInstall, "three-part and four-part versions compare equally");
    manifest.Version = "0.1.8";
    await updater.Check(sourceUrl, Fetch, CancellationToken.None);
    // Keep the old assembly loaded while atomically replacing it, as a real Emby process does.
    File.WriteAllBytes(target + ".previous", new byte[] { 1, 2, 3 });
    var notifications = 0;
    var mapped = WhileMapped(target, () => updater.Install(sourceUrl, manifest.Version, manifest.Sha256, Fetch, () => notifications++, CancellationToken.None));
    status = mapped.Item2;
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Check(notifications == 1 && status.RestartRequired && !status.CanInstall && status.State == "pending-restart", "successful update notifies Emby and requires restart");
    Check(File.ReadAllBytes(target).SequenceEqual(latest) && File.ReadAllBytes(target + ".previous").SequenceEqual(old), "atomic replacement preserves the previous DLL exactly");
    Check(mapped.Item1 == new Version(0, 1, 6, 0) && status.InstalledVersion == "0.1.8", "loaded and installed versions remain distinct");
    await Reject(Install, "updating twice before a restart is rejected");
    Check(new PluginUpdater(root, new Version(0, 1, 6, 0)).Status(sourceUrl).RestartRequired, "pending restart survives browser state loss");
    var restarted = new PluginUpdater(root, new Version(0, 1, 8, 0));
    Check(!(await restarted.Check(sourceUrl, Fetch, CancellationToken.None)).CanInstall && !restarted.Status(sourceUrl).RestartRequired, "restart loads the installed version and clears pending state");
    Check(!Directory.GetFiles(root, "*.download-*").Any(), "successful update also removes staging files");
    Console.WriteLine($"Passed {checks} plugin update checks on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
}
finally { Directory.Delete(root, true); }
