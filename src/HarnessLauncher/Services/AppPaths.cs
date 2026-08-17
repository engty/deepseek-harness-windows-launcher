using System.Security.AccessControl;
using System.Security.Principal;

namespace HarnessLauncher.Services;

/// <summary>
/// Windows port of AppPaths. The macOS version stores state under
/// ~/Library/Application Support/&lt;bundle id&gt;; on Windows everything lives
/// under %LOCALAPPDATA%\DeepSeekHarness.
/// </summary>
public sealed class AppPaths
{
    public const string BundleIdentifier = "com.harness.desktop.launcher";
    public const string DirectoryName = "DeepSeekHarness";

    public string ApplicationSupport { get; }
    public string Caches { get; }
    public string Logs { get; }
    public string Toolchain { get; }

    public AppPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, DirectoryName);
        ApplicationSupport = root;
        Caches = Path.Combine(root, "Cache");
        Logs = Path.Combine(root, "Logs");
        Toolchain = Path.Combine(ApplicationSupport, "toolchain");
    }

    public AppPaths(string applicationSupport, string caches, string logs)
    {
        ApplicationSupport = applicationSupport;
        Caches = caches;
        Logs = logs;
        Toolchain = Path.Combine(applicationSupport, "toolchain");
    }

    public string State => Path.Combine(ApplicationSupport, "state");
    public string Data => Path.Combine(ApplicationSupport, "data");
    public string ActiveDataSlot => Path.Combine(Data, "active");
    public string DshHome => Path.Combine(ActiveDataSlot, "dsh-home");
    public string PluginMetadata => Path.Combine(DshHome, "launcher", "plugin-metadata.json");
    public string ProfileWeb => Path.Combine(DshHome, "profiles", "web");
    public string Runtimes => Path.Combine(ApplicationSupport, "runtimes");
    public string ActiveRuntimeManifest => Path.Combine(State, "active-runtime.json");
    public string LastKnownGoodRuntimeManifest => Path.Combine(State, "last-known-good-runtime.json");
    public string Overlay => Path.Combine(State, "launcher-web-overlay.cordis.patch.yml");
    public string SidecarPid => Path.Combine(State, "harness-sidecar.pid");
    public string PluginStaging => Path.Combine(Caches, "plugin-staging");
    public string PluginOperationsLog => Path.Combine(Logs, "plugin-operations.log");
    public string Backups => Path.Combine(ApplicationSupport, "backups");
    public string Diagnostics => Path.Combine(ApplicationSupport, "diagnostics");
    public string DiagnosticsFile => Path.Combine(Diagnostics, "last-diagnostics.txt");
    public string WebView2UserData => Path.Combine(ApplicationSupport, "webview2");
    public string ProtectedCredentialStore => Path.Combine(State, "credentials");

    public void Prepare()
    {
        foreach (var directory in new[]
        {
            ApplicationSupport, Caches, Logs, State, Data, ActiveDataSlot,
            Runtimes, DshHome, Toolchain, PluginStaging, Backups, Diagnostics,
        })
        {
            Directory.CreateDirectory(directory);
        }
        // Sensitive trees must not be readable by other local users: dsh-home
        // holds the credentials file, diagnostics and logs hold
        // redacted-but-sensitive operation data. POSIX 0700 has no meaning on
        // Windows; the equivalent is an ACL granting only the current user.
        foreach (var directory in new[] { DshHome, Diagnostics, Logs })
        {
            TryRestrictToCurrentUser(directory);
        }
    }

    /// <summary>
    /// Replaces the directory ACL with full control for the current user only
    /// (plus SYSTEM), without inheritance. Best-effort: failures never block
    /// startup, matching the macOS `try? chmod 0700` behaviour.
    /// </summary>
    public static void TryRestrictToCurrentUser(string directory)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(directory)) return;
            var security = new DirectorySecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return;
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch
        {
            // Best effort only.
        }
    }
}
