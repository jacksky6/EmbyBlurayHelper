namespace EmbyBlurayHelper.ProbeWrapper.MediaFeatures;

internal sealed class MediaFeatureAttempt : IDisposable
{
    private readonly string path;
    private int completed;
    private string? failure;
    internal MediaFeatureOptions Options { get; }
    private MediaFeatureAttempt(MediaFeatureOptions options, string path)
    { this.path = path; Options = options with { Attempt = this }; }
    internal static MediaFeatureAttempt? Start(MediaFeatureOptions? options, string path) =>
        options?.Enabled == true ? new MediaFeatureAttempt(options, path) : null;
    internal void Complete() => completed++;
    internal void Fail(string message) => failure = message;
    public void Dispose()
    {
        try { MediaFeatureReceipt.Write(path, failure is null && completed > 0,
            failure ?? (completed > 0 ? "增强码流核验完成（包括无新增规格的结果）。" : "增强码流核验未完成。")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("EmbyBlurayHelper.MediaFeatures: could not save verification receipt: " + e.Message); }
    }
}
