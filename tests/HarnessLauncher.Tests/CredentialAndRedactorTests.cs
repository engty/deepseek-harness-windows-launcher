using Xunit;
using HarnessLauncher.Services;

namespace HarnessLauncher.Tests;

public class DeepSeekCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dsh-cred-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        var store = new DeepSeekCredentialStore();
        store.Write("sk-test-key-12345", _root);
        Assert.Equal("sk-test-key-12345", store.Read(_root));
    }

    [Fact]
    public void ReplacesExistingKeyWithoutTouchingOtherEntries()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".credentials.yaml"),
            "OTHER_KEY: keep-me\nDEEPSEEK_API_KEY: \"old\"\n");
        var store = new DeepSeekCredentialStore();
        store.Write("new-key", _root);
        var text = File.ReadAllText(Path.Combine(_root, ".credentials.yaml"));
        Assert.Contains("OTHER_KEY: keep-me", text);
        Assert.Contains("DEEPSEEK_API_KEY: \"new-key\"", text);
        Assert.DoesNotContain("old", text);
        Assert.Equal("new-key", store.Read(_root));
    }

    [Fact]
    public void ReadsSingleQuotedAndPlainScalars()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ".credentials.yaml");
        var store = new DeepSeekCredentialStore();

        File.WriteAllText(path, "DEEPSEEK_API_KEY: 'it''s-quoted'\n");
        Assert.Equal("it's-quoted", store.Read(_root));

        File.WriteAllText(path, "DEEPSEEK_API_KEY: plain-value # comment\n");
        Assert.Equal("plain-value", store.Read(_root));
    }

    [Fact]
    public void ReturnsNullWhenFileMissing()
    {
        Assert.Null(new DeepSeekCredentialStore().Read(_root));
    }

    [Fact]
    public void RejectsEmptyKey()
    {
        Assert.Throws<DeepSeekCredentialStoreException>(
            () => new DeepSeekCredentialStore().Write("", _root));
    }
}

public class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactsSkShapedKeys()
    {
        Assert.Equal("key: [REDACTED_API_KEY]",
            Support.SensitiveDataRedactor.Redact("key: sk-abcdefgh1234"));
    }

    [Fact]
    public void RedactsBearerHeaders()
    {
        var redacted = Support.SensitiveDataRedactor.Redact("Authorization: Bearer some-token-value");
        Assert.DoesNotContain("some-token-value", redacted);
        Assert.DoesNotContain("Bearer some", redacted);
    }

    [Fact]
    public void RedactsRegisteredLiteralSecrets()
    {
        Support.SensitiveDataRedactor.RegisterLiteralSecret("my-literal-secret-value");
        Assert.Equal("got [REDACTED_API_KEY] ok",
            Support.SensitiveDataRedactor.Redact("got my-literal-secret-value ok"));
    }

    [Fact]
    public void RedactsFieldNameForms()
    {
        var redacted = Support.SensitiveDataRedactor.Redact("{\"api_key\": \"abc123\"}");
        Assert.DoesNotContain("abc123", redacted);
    }
}