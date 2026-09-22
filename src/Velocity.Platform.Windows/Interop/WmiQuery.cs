using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

namespace Velocity.Platform.Windows.Interop;

/// <summary>One row returned by a WMI query, with conversions that never throw on a missing field.</summary>
/// <remarks>
/// WMI properties are absent, null or a different numeric width depending on the Windows edition,
/// the driver and whether the machine is virtual. Probes must degrade rather than fail, so every
/// accessor here returns null instead of throwing.
/// </remarks>
public sealed class WmiRecord
{
    private readonly ManagementBaseObject _source;

    internal WmiRecord(ManagementBaseObject source) => _source = source;

    /// <summary>Reads a string property.</summary>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public string? GetString(string name) => TryGet(name)?.ToString();

    /// <summary>Reads an unsigned 64 bit property, tolerating string and narrower numeric forms.</summary>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or unparsable.</returns>
    public ulong? GetUInt64(string name)
    {
        object? value = TryGet(name);
        return value switch
        {
            null => null,
            ulong typed => typed,
            long typed => typed < 0 ? null : (ulong)typed,
            uint typed => typed,
            int typed => typed < 0 ? null : (ulong)typed,
            string text when ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)
                => parsed,
            _ => null,
        };
    }

    /// <summary>Reads an unsigned 32 bit property.</summary>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or out of range.</returns>
    public uint? GetUInt32(string name)
    {
        ulong? value = GetUInt64(name);
        return value is null || value > uint.MaxValue ? null : (uint)value;
    }

    /// <summary>Reads a boolean property.</summary>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public bool? GetBoolean(string name) => TryGet(name) as bool?;

    /// <summary>Reads an array of unsigned 16 bit values, as used by chassis types.</summary>
    /// <param name="name">Property name.</param>
    /// <returns>The values, or an empty array when absent.</returns>
    public IReadOnlyList<ushort> GetUInt16Array(string name) =>
        TryGet(name) is ushort[] values ? values : Array.Empty<ushort>();

    private object? TryGet(string name)
    {
        try
        {
            return _source[name];
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}

/// <summary>Runs WMI queries and yields rows as <see cref="WmiRecord"/>.</summary>
[SupportedOSPlatform("windows")]
public static class WmiQuery
{
    /// <summary>Default CIM namespace.</summary>
    public const string CimV2Namespace = @"root\CIMV2";

    /// <summary>Storage management namespace, which carries reliable media and bus types.</summary>
    public const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    /// <summary>Namespace exposing Device Guard and virtualization based security state.</summary>
    public const string DeviceGuardNamespace = @"root\Microsoft\Windows\DeviceGuard";

    /// <summary>Runs a query and yields each row.</summary>
    /// <param name="query">WQL query text.</param>
    /// <param name="scope">CIM namespace, defaulting to <see cref="CimV2Namespace"/>.</param>
    /// <returns>The rows.</returns>
    /// <remarks>
    /// Rows are materialised into a list rather than streamed, so the underlying searcher is
    /// disposed before the caller starts work. Leaving a WMI enumerator open across caller code is
    /// a reliable way to leak a COM object and a worker thread.
    /// </remarks>
    public static IReadOnlyList<WmiRecord> Run(string query, string scope = CimV2Namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var records = new List<WmiRecord>();
        using var searcher = new ManagementObjectSearcher(scope, query);
        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementBaseObject row in results)
        {
            records.Add(new WmiRecord(row));
        }

        return records;
    }
}
