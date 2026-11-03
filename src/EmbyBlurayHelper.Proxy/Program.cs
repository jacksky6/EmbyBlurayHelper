using System.Net;
using EmbyBlurayHelper.Proxy;

var configPath = Path.Combine(AppContext.BaseDirectory, "emby-bluray-helper-proxy.json");
var config = ProxyConfiguration.Load(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(config.ListenUrl);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None
})
{
    Timeout = Timeout.InfiniteTimeSpan
});
builder.Services.AddMemoryCache(options => options.SizeLimit = 128);
builder.Services.AddSingleton<EmbyProxy>();

var app = builder.Build();
app.UseWebSockets();
app.Run(context => context.RequestServices.GetRequiredService<EmbyProxy>().HandleAsync(context));

app.Run();
