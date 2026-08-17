using System.Text;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public class CredentialStoreException : Exception
{
    public CredentialStoreException(string message) : base(message) { }
}

/// <summary>
/// Windows replacement for the macOS KeychainStore. The API key is protected
/// with DPAPI (ProtectedData, CurrentUser scope) and stored as a file under
/// the app's private state directory. Only the same Windows user account can
/// decrypt it — no admin rights, no Credential Manager prompts, and the file
/// is useless when copied to another machine or profile.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _filePath;

    public CredentialStore(string directory, string account)
    {
        var safeAccount = string.Concat(account.Split(Path.GetInvalidFileNameChars()));
        _filePath = Path.Combine(directory, safeAccount + ".cred");
    }

    /// <summary>
    /// Non-interactive read, mirroring the macOS `allowInteraction: false`
    /// contract: DPAPI CurrentUser decryption never prompts, so a failure is
    /// simply treated as "no credential".
    /// </summary>
    public string? Read()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var protectedBytes = File.ReadAllBytes(_filePath);
            var data = System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var data = System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            AppPaths.TryRestrictToCurrentUser(Path.GetDirectoryName(_filePath)!);
        }
        catch (Exception error)
        {
            throw new CredentialStoreException($"Windows 凭据保护失败：{error.Message}");
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch (Exception error)
        {
            throw new CredentialStoreException($"Windows 凭据删除失败：{error.Message}");
        }
    }
}
