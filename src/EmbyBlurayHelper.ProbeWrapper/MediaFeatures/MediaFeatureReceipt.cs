using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

// Shared with the in-process plugin. The wrapper runs under Emby's account,
// so its receipt is accessible without exposing the helper's credentials.
internal static class MediaFeatureReceipt
{
    internal static string SourcePath(string path)
    {
        if (Directory.Exists(path))
            path = Path.Combine(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("BDMV", StringComparison.OrdinalIgnoreCase) ? "index.bdmv" : Path.Combine("BDMV", "index.bdmv"));
        return Path.GetFullPath(path);
    }

    private static string Location(string path)
    {
        path = SourcePath(path);
        if (Path.DirectorySeparatorChar == '\\') path = path.ToUpperInvariant();
        using var sha = SHA256.Create();
        var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
        return Path.Combine(Path.GetTempPath(), "emby-bluray-helper-features", Environment.UserName, "receipt-" + key + ".txt");
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(SourcePath(path));
        return file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }

    internal static void Write(string path, bool success, string message)
    {
        var target = Location(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N");
        File.WriteAllLines(temporary, new[] { "1", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
            Stamp(path), success ? "success" : "failed", message.Replace('\r', ' ').Replace('\n', ' ') });
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target);
    }

    internal static bool Confirm(string path, DateTime startedUtc, out string message)
    {
        message = "增强探测未返回本次核验凭据，请确认 Helper 已更新并接入 Emby。";
        try
        {
            var lines = File.ReadAllLines(Location(path));
            if (lines.Length < 5 || lines[0] != "1" || !long.TryParse(lines[1], out var ticks) ||
                ticks < startedUtc.Ticks || lines[2] != Stamp(path)) return false;
            message = lines[4];
            return lines[3] == "success";
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        { return false; }
    }
}
