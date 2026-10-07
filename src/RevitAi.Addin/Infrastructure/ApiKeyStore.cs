using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RevitAi.Addin.Infrastructure;

/// <summary>
/// The user's own API keys, one per AI service (e.g. openai.key, gemini.key), encrypted with Windows DPAPI for the
/// current Windows user (ADR-029, ADR-046). Keys are never logged and never included in AI context.
/// </summary>
public sealed class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RevitAi.OpenAiKey.v1");

    private readonly string _directory;

    public ApiKeyStore(string directory)
    {
        _directory = directory;
    }

    public bool HasKey(string providerId) => File.Exists(PathFor(providerId));

    public void Save(string providerId, string apiKey)
    {
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(providerId), encrypted);
    }

    /// <summary>Returns null when no key is stored or it cannot be decrypted (e.g. copied from another Windows account).</summary>
    public string? TryLoad(string providerId)
    {
        try
        {
            string path = PathFor(providerId);
            return File.Exists(path)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string providerId) => File.Delete(PathFor(providerId));

    private string PathFor(string providerId) => Path.Combine(_directory, providerId + ".key");
}
