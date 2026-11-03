using System.Text;
using System.Text.Json;

namespace EmbyBlurayHelper.App;

internal static class EmbyJsonContent
{
    private static readonly JsonSerializerOptions Options = new();

    // Emby 4.10's plugin binder ignores chunked JSON without Content-Length.
    // Buffer these small control requests; preserve the PascalCase DTO member names.
    public static StringContent Create(object body) =>
        new(JsonSerializer.Serialize(body, Options), Encoding.UTF8, "application/json");
}
