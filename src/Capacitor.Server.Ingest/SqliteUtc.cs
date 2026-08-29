using System.Globalization;

namespace Capacitor.Server.Ingest;

internal static class SqliteUtc {
    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
