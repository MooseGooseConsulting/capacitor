using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Returns a caller-supplied PTY process so a test can control its output behaviour.</summary>
sealed class FixedPtyProcessFactory(IPtyProcess process) : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) => process;
}
