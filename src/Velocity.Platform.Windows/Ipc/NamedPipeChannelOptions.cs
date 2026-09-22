namespace Velocity.Platform.Windows.Ipc;

/// <summary>Settings shared by the helper's pipe server and the desktop client.</summary>
public sealed class NamedPipeChannelOptions
{
    /// <summary>Name of the named pipe, without the <c>\\.\pipe\</c> prefix.</summary>
    public string PipeName { get; set; } = "velocity-helper";

    /// <summary>How long the client waits for the helper to accept a connection.</summary>
    public int ConnectTimeoutMilliseconds { get; set; } = 3000;

    /// <summary>
    /// Full path of the executable the helper will accept connections from. When set, the helper
    /// resolves the calling process and refuses any other image.
    /// </summary>
    public string? ExpectedClientImagePath { get; set; }

    /// <summary>
    /// Expected Authenticode subject of the calling executable, for example
    /// <c>CN=Velocity Systems, O=Velocity Systems, C=...</c>. When set, the helper reads the
    /// signature of the calling image and refuses a mismatch.
    /// </summary>
    public string? ExpectedClientCertificateSubject { get; set; }
}
