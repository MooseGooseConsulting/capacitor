using System.Globalization;

namespace Capacitor.Server.Ingest;

internal static class EventTimestamp {
    public static string ToUtcString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
