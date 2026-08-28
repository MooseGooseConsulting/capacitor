using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryStorePaths {
    public static string DefaultRoot(ConfigRoot config) => config.Path("cache", "session-start-memory-v1");

    /// <summary>Separate from <see cref="DefaultRoot"/> so a nudge claim is neither counted against the
    /// lease store's entry caps nor visible to its sweep, which admits only record and temp names.</summary>
    public static string NudgeGateRoot(ConfigRoot config) => config.Path("cache", "session-start-nudge-v1");

    public static string ValidateRoot(string root) {
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        if (!OperatingSystem.IsWindows()) {
            try {
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch { }
        }
        var info = new DirectoryInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("SessionStart memory store root may not be a symlink or reparse point.");
        return full;
    }
}
