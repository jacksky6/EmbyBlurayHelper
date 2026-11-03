using System.Diagnostics;
using System.Text.Json;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

namespace EmbyBlurayHelper.ProbeWrapper;

internal static class ProbeProcess
{
    public static ProcessStartInfo CreateStartInfo(string executablePath)
        => EmbyToolProcess.CreateStartInfo(executablePath);

    public static async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool enableLanguagePatch = true,
        MediaFeatureOptions? mediaFeatures = null)
    {
        var captureOutput = (enableLanguagePatch || mediaFeatures?.Enabled == true) && BluRayLanguageAugmenter.ShouldCapture(arguments);
        using var capturedOutput = captureOutput ? new MemoryStream() : null;
        var stdout = captureOutput ? capturedOutput! : Console.OpenStandardOutput();
        string? selectedPlaylist = null;
        int exitCode;
        if (captureOutput && (BluRayLanguageAugmenter.GetIsoInput(arguments) is not null ||
                              BluRayLanguageAugmenter.GetBdmvIndexInput(arguments) is not null))
        {
            try
            {
                var isoPath = BluRayLanguageAugmenter.GetIsoInput(arguments);
                var result = isoPath is not null
                    ? await IsoMediaProbe.Run(executablePath, arguments, isoPath, null, cancellationToken, mediaFeatures)
                    : await IsoMediaProbe.RunDirectory(executablePath, arguments, BluRayLanguageAugmenter.GetBdmvIndexInput(arguments)!, cancellationToken, mediaFeatures);
                await capturedOutput!.WriteAsync(result.Output, cancellationToken);
                selectedPlaylist = result.PlaylistName;
                exitCode = 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync("EmbyBlurayHelper.ProbeWrapper: Blu-ray disc reader failed: " + exception.Message);
                exitCode = 1;
            }
        }
        else exitCode = await RunProcessAsync(executablePath, arguments, stdout, cancellationToken);

        if (captureOutput && exitCode == 0)
        {
            var originalOutput = capturedOutput!.ToArray();
            var finalOutput = originalOutput;
            try
            {
                // Directory probes already use the selected MPLS; do not merge languages
                // from unrelated extras when the selected title explicitly says "und".
                string? diagnostic = null;
                if (enableLanguagePatch && BluRayLanguageAugmenter.GetBdmvIndexInput(arguments) is null)
                    finalOutput = BluRayLanguageAugmenter.TryPatch(originalOutput, arguments, out diagnostic, selectedPlaylist);
                if (selectedPlaylist is null && BluRayLanguageAugmenter.GetMediaInput(arguments) is { } input && IsBdmvStream(input))
                    finalOutput = await MediaFeatureEnricher.Enrich(finalOutput, input, input, mediaFeatures, cancellationToken);
                if (!string.IsNullOrWhiteSpace(diagnostic))
                {
                    if (selectedPlaylist is not null) diagnostic = $"Selected playlist {selectedPlaylist}. " + diagnostic;
                    await Console.Error.WriteLineAsync("EmbyBlurayHelper.ProbeWrapper: " + diagnostic);
                }
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
        else if (captureOutput)
            await Console.OpenStandardOutput().WriteAsync(capturedOutput!.ToArray(), cancellationToken);

        return exitCode;
    }

    internal static bool HasVideo(byte[] output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            return json.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array &&
                streams.EnumerateArray().Any(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        }
        catch (JsonException) { return false; }
    }

    internal static bool IsBdmvStream(string input) =>
        (input.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) || input.StartsWith("concat:", StringComparison.OrdinalIgnoreCase)) &&
        input.Replace('\\', '/').Contains("/BDMV/STREAM/", StringComparison.OrdinalIgnoreCase);

    internal static async Task<int> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        Stream stdout,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(executablePath);
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

        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError(), cancellationToken);
        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask, stderrTask);
        return process.ExitCode;
    }

}
