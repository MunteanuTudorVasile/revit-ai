using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RevitAi.Addin.Infrastructure;

/// <summary>
/// The user's own OpenAI API key, encrypted with Windows DPAPI for the current Windows user (ADR-029).
/// The key is never logged and never included in AI context.
/// </summary>
public sealed class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RevitAi.OpenAiKey.v1");

    private readonly string _path;

    public ApiKeyStore(string path)
    {
        _path = path;
    }

    public bool HasKey => File.Exists(_path);

    public void Save(string apiKey)
    {
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, encrypted);
    }

    /// <summary>Returns null when no key is stored or it cannot be decrypted (e.g. copied from another Windows account).</summary>
    public string? TryLoad()
    {
        try
        {
            return File.Exists(_path)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete() => File.Delete(_path);
}
