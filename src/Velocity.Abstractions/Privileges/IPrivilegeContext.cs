namespace Velocity.Abstractions.Privileges;

/// <summary>How a privileged operation can be reached from the current process.</summary>
public enum PrivilegeChannel
{
    /// <summary>No privileged channel is available; privileged tweaks are not offered.</summary>
    None = 0,

    /// <summary>The current process is elevated and performs the operation directly.</summary>
    DirectElevation = 1,

    /// <summary>The operation is forwarded to the privileged helper service over IPC.</summary>
    HelperService = 2,
}

/// <summary>Describes the privileges available to the running process.</summary>
public interface IPrivilegeContext
{
    /// <summary><see langword="true"/> when the current process token is elevated.</summary>
    bool IsProcessElevated { get; }

    /// <summary>How privileged work can be performed right now.</summary>
    PrivilegeChannel Channel { get; }

    /// <summary>
    /// <see langword="true"/> when a privileged operation can be performed, whether directly or
    /// through the helper.
    /// </summary>
    bool CanElevate => Channel != PrivilegeChannel.None;
}
