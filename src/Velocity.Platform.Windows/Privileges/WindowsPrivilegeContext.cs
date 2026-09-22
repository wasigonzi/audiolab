using System;
using System.Runtime.Versioning;
using System.Security.Principal;
using Velocity.Abstractions.Privileges;

namespace Velocity.Platform.Windows.Privileges;

/// <summary>Reports how privileged work can be performed from this process.</summary>
/// <remarks>
/// The desktop application deliberately runs unelevated. Elevation is reached only through the
/// helper service, so in a normal installation <see cref="Channel"/> is
/// <see cref="PrivilegeChannel.HelperService"/> and never <see cref="PrivilegeChannel.DirectElevation"/>.
/// Direct elevation is still supported because the command line tool is sometimes run from an
/// administrator console during support work.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrivilegeContext : IPrivilegeContext
{
    private readonly Func<bool> _helperAvailable;

    /// <summary>Creates the context.</summary>
    /// <param name="helperAvailable">
    /// Callback reporting whether the helper channel can currently be reached. It is a callback
    /// rather than a captured boolean because the service can start or stop while the UI is open.
    /// </param>
    public WindowsPrivilegeContext(Func<bool> helperAvailable)
    {
        _helperAvailable = helperAvailable ?? throw new ArgumentNullException(nameof(helperAvailable));
        IsProcessElevated = DetectElevation();
    }

    /// <inheritdoc />
    public bool IsProcessElevated { get; }

    /// <inheritdoc />
    public PrivilegeChannel Channel => IsProcessElevated
        ? PrivilegeChannel.DirectElevation
        : _helperAvailable()
            ? PrivilegeChannel.HelperService
            : PrivilegeChannel.None;

    private static bool DetectElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
