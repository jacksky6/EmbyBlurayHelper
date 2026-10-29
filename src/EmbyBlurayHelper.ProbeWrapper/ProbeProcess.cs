using System.Diagnostics;

namespace EmbyBlurayHelper.ProbeWrapper;

internal static class ProbeProcess
{
    public static async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start upstream ffprobe: {executablePath}");
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(
            Console.OpenStandardOutput(), cancellationToken);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(
            Console.OpenStandardError(), cancellationToken);

        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask, stderrTask);
        return process.ExitCode;
    }
}
