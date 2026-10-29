using System.Diagnostics;
using System.Text.Json;

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

        var captureOutput = BluRayLanguageAugmenter.ShouldCapture(arguments);
        using var capturedOutput = captureOutput ? new MemoryStream() : null;
        var stdout = captureOutput ? capturedOutput! : Console.OpenStandardOutput();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(
            Console.OpenStandardError(), cancellationToken);

        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask, stderrTask);

        if (captureOutput)
        {
            var originalOutput = capturedOutput!.ToArray();
            var finalOutput = originalOutput;
            try
            {
                finalOutput = BluRayLanguageAugmenter.TryPatch(originalOutput, arguments);
            }
            catch (JsonException)
            {
                // Non-JSON output is passed through unchanged.
            }
            catch (Exception exception)
            {
                await Console.Error.WriteLineAsync(
                    $"EmbyBlurayHelper.ProbeWrapper: Blu-ray language enrichment skipped: {exception.Message}");
            }

            await Console.OpenStandardOutput().WriteAsync(finalOutput, cancellationToken);
        }

        return process.ExitCode;
    }
}
