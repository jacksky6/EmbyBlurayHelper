using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmbyBlurayHelper.App;

internal sealed record LogEntry(DateTimeOffset Time, string Level, string Source, string Message);

internal sealed class HelperLog : ILoggerProvider
{
    private readonly object gate = new();
    private readonly Queue<LogEntry> entries = new();
    private readonly string directory;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private long requests, active, patchedTables, patchedBytes;
    public HelperLog(string dataDirectory)
    {
        directory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(directory);
    }

    public void Add(string level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, Redact(message));
        lock (gate)
        {
            entries.Enqueue(entry);
            while (entries.Count > 400) entries.Dequeue();
            try
            {
                var path = Path.Combine(directory, "helper-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, SettingsStore.Json).Replace("\r", "").Replace("\n", "") + Environment.NewLine);
                foreach (var old in new DirectoryInfo(directory).GetFiles("helper-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(14))
                    old.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public LogEntry[] Entries()
    {
        lock (gate) return ReadEntries();
    }

    public int Clear()
    {
        lock (gate)
        {
            var files = new DirectoryInfo(directory).GetFiles("helper-*")
                .Where(file => Regex.IsMatch(file.Name, @"^helper-\d{8}\.jsonl(?:\.previous)?$")).ToArray();
            foreach (var file in files) file.Delete();
            entries.Clear();
            return files.Length;
        }
    }

    private LogEntry[] ReadEntries()
    {
        var result = new List<LogEntry>();
        result.AddRange(entries);
        foreach (var file in new DirectoryInfo(directory).GetFiles("helper-*").OrderByDescending(f => f.LastWriteTimeUtc).Take(2))
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var partial = stream.Length > 256 * 1024;
                if (partial) stream.Seek(-256 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                if (partial) reader.ReadLine();
                while (reader.ReadLine() is string line)
                    try { if (JsonSerializer.Deserialize<LogEntry>(line, SettingsStore.Json) is { } entry) result.Add(entry); }
                    catch (JsonException) { }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.Distinct().OrderBy(e => e.Time).TakeLast(400).ToArray();
    }
    public void BeginRequest() { Interlocked.Increment(ref requests); Interlocked.Increment(ref active); }
    public void EndRequest() => Interlocked.Decrement(ref active);
    public object Status()
    {
        using var process = Process.GetCurrentProcess();
        return new { started, requests = Interlocked.Read(ref requests), active = Interlocked.Read(ref active),
            patchedTables = Interlocked.Read(ref patchedTables), patchedBytes = Interlocked.Read(ref patchedBytes),
            memoryMiB = Math.Round(process.WorkingSet64 / 1048576d, 1), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() };
    }

    public static string Redact(string message) => Regex.Replace(message,
        """(?i)(api_key|x-emby-token|token|password|authorization)([\s=:\"]+)([^\s&\",}]+)""", "$1$2[hidden]");
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { }

    private sealed class Sink(HelperLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || category.StartsWith("Microsoft.AspNetCore")) return;
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                var values = fields.ToDictionary(p => p.Key, p => p.Value);
                if (values.TryGetValue("Patched", out var tables)) Interlocked.Add(ref owner.patchedTables, Convert.ToInt64(tables));
                if (values.TryGetValue("Length", out var bytes)) Interlocked.Add(ref owner.patchedBytes, Convert.ToInt64(bytes));
            }
            owner.Add(level.ToString(), category.Split('.').Last(), formatter(state, null));
        }
    }
}
