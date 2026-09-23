using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Ipc.Authorization;
using Velocity.Ipc.Protocol;
using Velocity.Platform.Windows.Ipc;

namespace Velocity.Platform.Windows.State;

/// <summary>
/// Reads and writes registry values on behalf of the snapshot, apply and rollback engines.
/// </summary>
/// <remarks>
/// <para>
/// State keys use the form <c>registry://HIVE\key\path#ValueName</c>, for example
/// <c>registry://HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl#Win32PrioritySeparation</c>.
/// </para>
/// <para>
/// Writes to <c>HKLM</c> need elevation. When the process is not elevated the write is forwarded to
/// the helper service, which applies <see cref="PrivilegedOperationPolicy"/> again on its side. The
/// policy is also evaluated here, before the call is made, so that a refusal is reported the same
/// way whether or not the helper is in the path.
/// </para>
/// <para>
/// A value captured as <see cref="StateValueKind.Absent"/> is restored by deleting the value, which
/// is what keeps "the user never had this setting" reversible.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RegistryStateProvider : IStateProvider
{
    /// <summary>Scheme this provider answers for.</summary>
    public const string Scheme = "registry";

    private readonly IPrivilegeContext _privileges;
    private readonly IPrivilegedChannel? _channel;
    private readonly ILogger<RegistryStateProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="privileges">Privileges available to the current process.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="channel">
    /// Channel to the privileged helper, or <see langword="null"/> when the process is expected to
    /// be elevated (as in the helper itself and the administrative command line tool).
    /// </param>
    public RegistryStateProvider(
        IPrivilegeContext privileges,
        ILogger<RegistryStateProvider> logger,
        IPrivilegedChannel? channel = null)
    {
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = channel;
    }

    /// <inheritdoc />
    public string ProviderId => Scheme;

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public bool RequiresElevation(StateKey key)
    {
        (string hive, _) = SplitPath(key.Path);
        return !string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (string hive, string subKeyPath) = SplitPath(key.Path);

        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryRead(hive, subKeyPath);
        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException($"Reading '{key}' is refused: {decision.Reason}");
        }

        using RegistryKey root = OpenHive(hive);
        using RegistryKey? subKey = root.OpenSubKey(subKeyPath, writable: false);

        if (subKey is null || key.Item is null)
        {
            return Task.FromResult(StateValue.Absent);
        }

        object? value = subKey.GetValue(key.Item, defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        if (value is null)
        {
            return Task.FromResult(StateValue.Absent);
        }

        RegistryValueKind kind = subKey.GetValueKind(key.Item);
        return Task.FromResult(ToStateValue(kind, value));
    }

    /// <inheritdoc />
    public async Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken)
    {
        (string hive, string subKeyPath) = SplitPath(key.Path);

        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryWrite(hive, subKeyPath, key.Item);
        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException($"Writing '{key}' is refused: {decision.Reason}");
        }

        if (key.Item is null)
        {
            throw new ArgumentException("A registry state key must name a value.", nameof(key));
        }

        if (RequiresElevation(key) && !_privileges.IsProcessElevated)
        {
            await WriteThroughHelperAsync(key, hive, subKeyPath, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        WriteDirect(hive, subKeyPath, key.Item, value);
    }

    /// <summary>
    /// Performs the write with the current process's privileges. Used by the helper's own handler,
    /// which has already authorized the request.
    /// </summary>
    /// <param name="hive">Hive short name.</param>
    /// <param name="subKeyPath">Key path inside the hive.</param>
    /// <param name="valueName">Value name.</param>
    /// <param name="value">Value to write, or absent to delete.</param>
    public static void WriteDirect(string hive, string subKeyPath, string valueName, StateValue value)
    {
        using RegistryKey root = OpenHive(hive);

        if (value.IsAbsent)
        {
            using RegistryKey? existing = root.OpenSubKey(subKeyPath, writable: true);
            existing?.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        using RegistryKey subKey = root.CreateSubKey(subKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create '{hive}\\{subKeyPath}'.");

        (object data, RegistryValueKind kind) = FromStateValue(value);
        subKey.SetValue(valueName, data, kind);
    }

    private async Task WriteThroughHelperAsync(
        StateKey key,
        string hive,
        string subKeyPath,
        StateValue value,
        CancellationToken cancellationToken)
    {
        if (_channel is null || !_channel.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Writing '{key}' requires the privileged helper, which is not running.");
        }

        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hive"] = hive,
            ["path"] = subKeyPath,
            ["name"] = key.Item ?? string.Empty,
            ["kind"] = value.Kind.ToString(),
            ["data"] = value.Data ?? string.Empty,
        };

        IpcResponse response = await _channel
            .InvokeAsync(IpcOperations.RegistryWrite, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"The privileged helper refused to write '{key}': {response.ErrorCode} {response.ErrorMessage}");
        }

        _logger.LogDebug("Wrote {Key} through the privileged helper.", key.ToString());
    }

    /// <summary>Splits a state key path into its hive and sub key components.</summary>
    /// <param name="path">Path from the state key.</param>
    /// <returns>The hive short name and the remaining key path.</returns>
    /// <exception cref="ArgumentException">The path does not start with a known hive.</exception>
    public static (string Hive, string SubKeyPath) SplitPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalized = path.Replace('/', '\\').Trim('\\');
        int separator = normalized.IndexOf('\\', StringComparison.Ordinal);

        return separator < 0
            ? (normalized, string.Empty)
            : (normalized[..separator], normalized[(separator + 1)..]);
    }

    private static RegistryKey OpenHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Registry.Users,
        _ => throw new ArgumentException($"'{hive}' is not a supported registry hive.", nameof(hive)),
    };

    /// <summary>Converts a registry value and its kind into the transport neutral representation.</summary>
    /// <param name="kind">Registry value kind.</param>
    /// <param name="value">Value as returned by the registry API.</param>
    /// <returns>The captured value.</returns>
    public static StateValue ToStateValue(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.String => new StateValue(StateValueKind.String, value.ToString()),
        RegistryValueKind.ExpandString => new StateValue(StateValueKind.ExpandString, value.ToString()),
        RegistryValueKind.MultiString => new StateValue(
            StateValueKind.MultiString, string.Join('\n', (string[])value)),
        RegistryValueKind.DWord => new StateValue(
            StateValueKind.UInt32,
            unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture))
                .ToString(CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => new StateValue(
            StateValueKind.UInt64,
            unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture))
                .ToString(CultureInfo.InvariantCulture)),
        RegistryValueKind.Binary => new StateValue(StateValueKind.Binary, Convert.ToBase64String((byte[])value)),
        _ => new StateValue(StateValueKind.String, value.ToString()),
    };

    /// <summary>Converts a captured value back into the data and kind the registry expects.</summary>
    /// <param name="value">Captured value.</param>
    /// <returns>The data object and its registry kind.</returns>
    /// <exception cref="ArgumentException">The value cannot be represented in the registry.</exception>
    public static (object Data, RegistryValueKind Kind) FromStateValue(StateValue value) => value.Kind switch
    {
        StateValueKind.String => (value.Data ?? string.Empty, RegistryValueKind.String),
        StateValueKind.ExpandString => (value.Data ?? string.Empty, RegistryValueKind.ExpandString),
        StateValueKind.MultiString => (
            (value.Data ?? string.Empty).Split('\n', StringSplitOptions.None),
            RegistryValueKind.MultiString),
        StateValueKind.UInt32 => (
            unchecked((int)uint.Parse(value.Data ?? "0", CultureInfo.InvariantCulture)),
            RegistryValueKind.DWord),
        StateValueKind.UInt64 => (
            unchecked((long)ulong.Parse(value.Data ?? "0", CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord),
        StateValueKind.Binary => (Convert.FromBase64String(value.Data ?? string.Empty), RegistryValueKind.Binary),
        _ => throw new ArgumentException(
            $"A value of kind {value.Kind} cannot be written to the registry.", nameof(value)),
    };
}
