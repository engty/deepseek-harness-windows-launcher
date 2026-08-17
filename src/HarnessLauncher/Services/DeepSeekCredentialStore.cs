using System.Text.Json;

namespace HarnessLauncher.Services;

public class DeepSeekCredentialStoreException : Exception
{
    public DeepSeekCredentialStoreException(string message) : base(message) { }

    public static readonly DeepSeekCredentialStoreException InvalidValue =
        new("DeepSeek API Key 格式无效。");
    public static readonly DeepSeekCredentialStoreException UnreadableDocument =
        new("Harness 凭据文件无法解析，请通过更换 API Key 重新保存。");
    public static DeepSeekCredentialStoreException WriteFailed(string message) =>
        new($"无法同步 Harness 凭据文件：{message}");
}

/// <summary>
/// Bridges the app's DPAPI credential binding with Harness's standard
/// credential file. The file contains the provider's normal
/// DEEPSEEK_API_KEY reference, so the Web Models page and the native balance
/// query always resolve one value. Direct port of the macOS store; POSIX
/// 0600 is replaced by ACL restriction of the containing dsh-home directory.
/// </summary>
public sealed class DeepSeekCredentialStore
{
    public const string Reference = "DEEPSEEK_API_KEY";
    public const string FileName = ".credentials.yaml";

    public string? Read(string dshHome)
    {
        var url = CredentialsUrl(dshHome);
        if (!File.Exists(url)) return null;
        // Never follow a symlink/reparse point planted at the credentials
        // path: reading through it could surface unrelated file contents as
        // the API key.
        if (IsReparsePoint(url)) return null;
        string text;
        try
        {
            text = File.ReadAllText(url);
        }
        catch
        {
            throw DeepSeekCredentialStoreException.UnreadableDocument;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimStart('﻿');
            if (line.StartsWith(' ') || line.StartsWith('\t')) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            if (key != Reference) continue;
            var scalar = line[(colon + 1)..].Trim();
            if (scalar.Length == 0) throw DeepSeekCredentialStoreException.InvalidValue;
            return DecodeScalar(scalar);
        }
        return null;
    }

    public void Write(string apiKey, string dshHome)
    {
        if (string.IsNullOrEmpty(apiKey)) throw DeepSeekCredentialStoreException.InvalidValue;
        Directory.CreateDirectory(dshHome);
        AppPaths.TryRestrictToCurrentUser(dshHome);

        var url = CredentialsUrl(dshHome);
        var existing = File.Exists(url) ? File.ReadAllText(url) : "";
        var lines = existing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var replacement = $"{Reference}: {EncodeScalar(apiKey)}";
        var replaced = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith(' ') || line.StartsWith('\t')) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (line[..colon].Trim() != Reference) continue;
            lines[i] = replacement;
            replaced = true;
            break;
        }

        if (!replaced)
        {
            if (existing.Length > 0 && !existing.EndsWith('\n')) lines.Add("");
            lines.Add(replacement);
        }

        var text = string.Join('\n', lines).Trim('\n') + "\n";
        WriteOwnerOnly(text, url);
    }

    private static string CredentialsUrl(string dshHome) => Path.Combine(dshHome, FileName);

    private static string DecodeScalar(string scalar)
    {
        if (scalar.StartsWith('"'))
        {
            try
            {
                var value = JsonSerializer.Deserialize<string>(scalar);
                if (string.IsNullOrEmpty(value)) throw DeepSeekCredentialStoreException.InvalidValue;
                return value;
            }
            catch (JsonException)
            {
                throw DeepSeekCredentialStoreException.InvalidValue;
            }
        }

        if (scalar.StartsWith('\''))
        {
            if (!scalar.EndsWith('\'') || scalar.Length < 2)
            {
                throw DeepSeekCredentialStoreException.InvalidValue;
            }
            var value = scalar[1..^1].Replace("''", "'");
            if (value.Length == 0) throw DeepSeekCredentialStoreException.InvalidValue;
            return value;
        }

        var plain = scalar.Split(" #", 2)[0].Trim();
        if (plain.Length == 0) throw DeepSeekCredentialStoreException.InvalidValue;
        return plain;
    }

    private static string EncodeScalar(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
        return $"\"{escaped}\"";
    }

    private static void WriteOwnerOnly(string text, string url)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(url)!,
            $".credentials-{Guid.NewGuid():N}.tmp");
        try
        {
            // The containing dsh-home directory is ACL-restricted to the
            // current user (see AppPaths.Prepare), which is the Windows
            // equivalent of the macOS 0600 owner-only file.
            File.WriteAllText(temporary, text);
            if (IsReparsePoint(url))
            {
                File.Delete(url);
            }
            File.Move(temporary, url, overwrite: true);
        }
        catch (DeepSeekCredentialStoreException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw DeepSeekCredentialStoreException.WriteFailed(error.Message);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }
}
