using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Power;
using Velocity.Abstractions.Privileges;
using Velocity.Ipc.Protocol;
using Velocity.Platform.Windows.Interop;
using Velocity.Platform.Windows.Ipc;

namespace Velocity.Platform.Windows.Power;

/// <summary>
/// Reads and writes power configuration through the documented <c>powrprof.dll</c> API.
/// </summary>
/// <remarks>
/// <para>
/// Every operation here goes through a published entry point:
/// <c>PowerGetActiveScheme</c>, <c>PowerSetActiveScheme</c>, <c>PowerReadACValueIndex</c> and
/// <c>PowerWriteACValueIndex</c>. There is no registry poking under
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Power</c>: writing those keys behind the power service's
/// back leaves the cached policy and the stored policy disagreeing, which is exactly the kind of
/// half-applied state this product exists to avoid.
/// </para>
/// <para>
/// <b>Only the AC side is written.</b> <c>PowerWriteDCValueIndex</c> is deliberately not called.
/// Changing what a laptop does on battery is a battery life decision, and a gaming optimizer has no
/// business making it without being asked.
/// </para>
/// <para>
/// A setting the platform does not expose returns <see langword="null"/> from
/// <see cref="ReadAcValueAsync"/>, which the snapshot layer records as absent. Restoring an absent
/// value writes nothing, so a machine that never exposed core parking does not acquire a core
/// parking value because this product ran.
/// </para>
/// <para>
/// <b>Elevation.</b> <c>PowerSetActiveScheme</c> and <c>PowerWriteACValueIndex</c> need
/// administrative rights for the machine wide schemes this product targets. When the desktop
/// process is not elevated, both are forwarded to the privileged helper, which evaluates the power
/// allow list again on its own side. Reads are always performed in process, because they need no
/// privileges and a read that depends on a running service would make detection unreliable.
/// </para>
/// <para>
/// <b>Write ordering.</b> <c>PowerWriteACValueIndex</c> updates the stored scheme but does not make
/// the running system re-read it. When the edited scheme is the active one, the scheme is
/// re-activated afterwards so the change is actually in effect; otherwise
/// <see cref="Velocity.Abstractions.Tweaks.ITweak.VerifyAsync"/> would read back the new value from
/// storage while the machine kept running on the old one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerConfigurationController : IPowerConfigurationController
{
    private readonly ILogger<WindowsPowerConfigurationController> _logger;
    private readonly IPrivilegeContext? _privileges;
    private readonly IPrivilegedChannel? _channel;

    /// <summary>Creates the controller.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="privileges">
    /// Privileges available to the current process, or <see langword="null"/> when the process is
    /// expected to be elevated, as it is inside the helper itself.
    /// </param>
    /// <param name="channel">Channel to the privileged helper, when one is available.</param>
    public WindowsPowerConfigurationController(
        ILogger<WindowsPowerConfigurationController> logger,
        IPrivilegeContext? privileges = null,
        IPrivilegedChannel? channel = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _privileges = privileges;
        _channel = channel;
    }

    private bool MustForward => _privileges is not null && !_privileges.IsProcessElevated;

    /// <inheritdoc />
    public Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetActiveScheme());
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveSchemeAsync(Guid schemeGuid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (MustForward)
        {
            return await ForwardAsync(
                IpcOperations.PowerSetActiveScheme,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scheme"] = schemeGuid.ToString("D", CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);
        }

        uint result = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, in schemeGuid);

        if (result != NativeMethods.ErrorSuccess)
        {
            _logger.LogWarning(
                "PowerSetActiveScheme({Scheme}) failed with error {Error}.", schemeGuid, result);
            return false;
        }

        // Confirm against the machine rather than trusting the return code: the caller reports this
        // to the user as a change that happened.
        return GetActiveScheme() == schemeGuid;
    }

    /// <inheritdoc />
    public Task<uint?> ReadAcValueAsync(
        Guid schemeGuid,
        Guid subgroupGuid,
        Guid settingGuid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        uint result = NativeMethods.PowerReadACValueIndex(
            IntPtr.Zero, in schemeGuid, in subgroupGuid, in settingGuid, out uint value);

        if (result == NativeMethods.ErrorSuccess)
        {
            return Task.FromResult<uint?>(value);
        }

        _logger.LogDebug(
            "Power setting {Setting} in subgroup {Subgroup} is not exposed (error {Error}).",
            settingGuid,
            subgroupGuid,
            result);

        return Task.FromResult<uint?>(null);
    }

    /// <inheritdoc />
    public async Task<bool> WriteAcValueAsync(
        Guid schemeGuid,
        Guid subgroupGuid,
        Guid settingGuid,
        uint value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (MustForward)
        {
            return await ForwardAsync(
                IpcOperations.PowerWriteAcValue,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scheme"] = schemeGuid.ToString("D", CultureInfo.InvariantCulture),
                    ["subgroup"] = subgroupGuid.ToString("D", CultureInfo.InvariantCulture),
                    ["setting"] = settingGuid.ToString("D", CultureInfo.InvariantCulture),
                    ["value"] = value.ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);
        }

        uint result = NativeMethods.PowerWriteACValueIndex(
            IntPtr.Zero, in schemeGuid, in subgroupGuid, in settingGuid, value);

        if (result != NativeMethods.ErrorSuccess)
        {
            _logger.LogWarning(
                "PowerWriteACValueIndex({Setting}) failed with error {Error}.", settingGuid, result);
            return false;
        }

        // A stored value only becomes the running policy when the scheme is activated.
        if (GetActiveScheme() == schemeGuid)
        {
            uint activate = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, in schemeGuid);

            if (activate != NativeMethods.ErrorSuccess)
            {
                _logger.LogWarning(
                    "Wrote {Setting} but re-activating scheme {Scheme} failed with error {Error}; " +
                    "the value is stored but may not be in effect.",
                    settingGuid,
                    schemeGuid,
                    activate);
            }
        }

        return true;
    }

    private async Task<bool> ForwardAsync(
        string operation,
        Dictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        if (_channel is null || !_channel.IsAvailable)
        {
            throw new InvalidOperationException(
                $"'{operation}' requires the privileged helper, which is not running.");
        }

        IpcResponse response = await _channel
            .InvokeAsync(operation, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"The privileged helper refused '{operation}': {response.ErrorCode} {response.ErrorMessage}");
        }

        _logger.LogDebug("Performed {Operation} through the privileged helper.", operation);
        return true;
    }

    private static Guid GetActiveScheme()
    {
        uint result = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer);

        if (result != NativeMethods.ErrorSuccess || pointer == IntPtr.Zero)
        {
            return Guid.Empty;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            NativeMethods.LocalFree(pointer);
        }
    }
}
