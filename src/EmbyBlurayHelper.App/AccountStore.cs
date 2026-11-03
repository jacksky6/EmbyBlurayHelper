using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyBlurayHelper.App;

internal sealed record ManagementAccount
{
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = "123456";
    public string Revision { get; init; } = Guid.NewGuid().ToString("N");
}

internal sealed class AccountStore
{
    private readonly object gate = new();
    private ManagementAccount current;
    public string FilePath { get; }
    public ManagementAccount Current => Volatile.Read(ref current);

    public AccountStore(string dataDirectory)
    {
        FilePath = Path.Combine(dataDirectory, "account.json");
        current = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<ManagementAccount>(File.ReadAllText(FilePath), SettingsStore.Json)
                ?? throw new InvalidDataException("登录账号文件为空。")
            : new ManagementAccount();
        Validate(current.Username, current.Password);
        if (!File.Exists(FilePath)) Write(current);
    }

    public void Save(string username, string password)
    {
        Validate(username, password);
        lock (gate)
        {
            var account = new ManagementAccount { Username = username, Password = password };
            Write(account);
            Volatile.Write(ref current, account);
        }
    }

    private void Write(ManagementAccount account)
    {
        var temporary = FilePath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(account, SettingsStore.Json));
        SettingsStore.Restrict(temporary);
        File.Move(temporary, FilePath, true);
    }

    public static bool Matches(ManagementAccount account, string? username, string? password) =>
        Equal(username, account.Username) & Equal(password, account.Password);

    public static string SessionStamp(ManagementAccount account) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(account)));

    private static bool Equal(string? value, string expected) => value is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static void Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128)
            throw new ArgumentException("用户名不能为空，且不能超过 128 个字符。");
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024)
            throw new ArgumentException("密码不能为空，且不能超过 1024 个字符。");
    }
}
