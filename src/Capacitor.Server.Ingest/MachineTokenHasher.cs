using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Server.Ingest;

/// <summary>Only the hash is ever persisted, so a database read can never recover an issued node token.</summary>
public static class MachineTokenHasher {
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
