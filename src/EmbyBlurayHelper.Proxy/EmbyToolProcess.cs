using System.Diagnostics;

namespace EmbyBlurayHelper.Proxy;

internal static class EmbyToolProcess
{
    public static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsLinux())
        {
            var bin = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
            var packageRoot = Path.GetDirectoryName(bin);
            // DSM's Emby binaries use a relative ELF interpreter.
            if (Path.GetFileName(bin) == "bin" && packageRoot is not null &&
                File.Exists(Path.Combine(packageRoot, "lib", "ld-linux-x86-64.so.2")))
                info.WorkingDirectory = packageRoot;
        }
        return info;
    }
}
