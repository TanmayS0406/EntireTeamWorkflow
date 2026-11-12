using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Etw.Shared;

namespace Etw.Hosting;

/// <summary>
/// Keeps one small secret document (OAuth tokens, webhook URLs) in %ProgramData%\EntireTeamWorkflow\secrets, encrypted
/// with DPAPI for the account the service runs as and with an app-specific entropy value.
/// </summary>
public sealed class SecretStore<T> where T : class
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EntireTeamWorkflow.Secrets.v1");
    private readonly string _path;
    private readonly object _gate = new();

    public SecretStore(string name)
    {
        var folder = EtwPaths.Secrets;
        if (OperatingSystem.IsWindows())
        {
            try { FileAcl.Restrict(new DirectoryInfo(folder)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _path = Path.Combine(folder, name + ".dat");
    }

    public T? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var bytes = File.ReadAllBytes(_path);
                var plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes;
                return JsonSerializer.Deserialize<T>(plain, EtwJson.Options);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
            {
                return null; // Unreadable (different account, corrupt): behave as "not configured".
            }
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(value, EtwJson.Options);
            var bytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser) : plain;
            File.WriteAllBytes(_path, bytes);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }
}
