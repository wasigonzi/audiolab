using System;
using System.Data;
using System.Globalization;

namespace Velocity.Data;

/// <summary>
/// Converts between CLR values and the textual forms stored in SQLite.
/// </summary>
/// <remarks>
/// SQLite has no native date or GUID type. Storing both as fixed format invariant strings keeps
/// the database readable by any SQLite tool during support work, and keeps ordering by timestamp
/// correct as a plain string comparison.
/// </remarks>
internal static class SqliteValueConverter
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK";

    internal static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    internal static string? ToTextOrNull(DateTimeOffset? value) =>
        value is null ? null : ToText(value.Value);

    internal static DateTimeOffset ToTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static DateTimeOffset GetTimestamp(this IDataRecord record, int ordinal) =>
        ToTimestamp(record.GetString(ordinal));

    internal static DateTimeOffset? GetNullableTimestamp(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : ToTimestamp(record.GetString(ordinal));

    internal static string ToText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    internal static string? ToTextOrNull(Guid? value) => value is null ? null : ToText(value.Value);

    internal static Guid GetGuid(this IDataRecord record, int ordinal) =>
        Guid.ParseExact(record.GetString(ordinal), "D");
}
