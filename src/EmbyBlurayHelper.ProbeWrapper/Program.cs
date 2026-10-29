using System.Reflection;

namespace EmbyBlurayHelper.ProbeWrapper;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--helper-version")
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            Console.Out.WriteLine($"EmbyBlurayHelper.ProbeWrapper {version}");
            return 0;
        }

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        try
        {
            var upstreamPath = await ProbeConfiguration.ResolveUpstreamPathAsync(cancellationSource.Token);
            var probeArguments = args.Length == 1 && args[0] == "--check-upstream"
                ? new[] { "-version" }
                : args;

            return await ProbeProcess.RunAsync(upstreamPath, probeArguments, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"EmbyBlurayHelper.ProbeWrapper: {exception.Message}");
            return 1;
        }
    }
}
