using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Ipc;

namespace Velocity.Platform.Windows.Ipc;

/// <summary>
/// Accepts desktop connections inside the privileged helper.
/// </summary>
/// <remarks>
/// <para>
/// The pipe's ACL is the first line of defence. SYSTEM and Administrators get full control;
/// authenticated users get only the rights needed to open the pipe and exchange messages. They
/// cannot change the ACL, and a sandboxed or anonymous token cannot connect at all.
/// </para>
/// <para>
/// The ACL alone is not enough, because every interactive user on the machine is an authenticated
/// user. Two further checks run on accept: the calling process image must match the configured
/// path, and, when a signer is configured, that image's Authenticode subject must match too. What
/// finally bounds the damage is the operation policy, which runs per request and is the reason a
/// compromised desktop process still cannot write to Defender or the mitigation settings.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeServerHost
{
    private const int MaxConcurrentConnections = 4;

    private readonly IpcServer _server;
    private readonly NamedPipeChannelOptions _options;
    private readonly ILogger<NamedPipeServerHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="server">Request dispatcher.</param>
    /// <param name="options">Pipe settings.</param>
    /// <param name="logger">Logger.</param>
    public NamedPipeServerHost(
        IpcServer server,
        NamedPipeChannelOptions options,
        ILogger<NamedPipeServerHost> logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Accepts and serves connections until cancelled.</summary>
    /// <param name="cancellationToken">Token used to stop the host.</param>
    /// <returns>A task that completes when the host stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Privileged helper listening on pipe {PipeName}.", _options.PipeName);

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = CreateServerStream();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                if (!AuthorizeCaller(pipe))
                {
                    pipe.Disconnect();
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                NamedPipeServerStream accepted = pipe;
                pipe = null;

                // Each connection is served on its own task so a slow client cannot block the
                // accept loop. Concurrency is bounded by the pipe instance count.
                _ = ServeConnectionAsync(accepted, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to accept a privileged connection.");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        _logger.LogInformation("Privileged helper stopped listening.");
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await _server.ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A privileged connection ended with an error.");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var security = new PipeSecurity();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(
            new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Exactly what a desktop client needs to talk: connect, read, write. Not CreateNewInstance,
        // so another process cannot stand up a rogue instance of the same pipe name, and not
        // ChangePermissions or TakeOwnership.
        security.AddAccessRule(new PipeAccessRule(
            authenticatedUsers,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _options.PipeName,
            PipeDirection.InOut,
            MaxConcurrentConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private bool AuthorizeCaller(NamedPipeServerStream pipe)
    {
        if (_options.ExpectedClientImagePath is null && _options.ExpectedClientCertificateSubject is null)
        {
            // No client identity configured: the pipe ACL and the per operation policy are the
            // controls in effect. This is the supported configuration for development builds only.
            _logger.LogDebug("No client identity is configured; accepting on ACL and policy alone.");
            return true;
        }

        string? imagePath = ResolveClientImagePath(pipe);

        if (imagePath is null)
        {
            _logger.LogWarning("Refused a connection whose calling process could not be resolved.");
            return false;
        }

        if (_options.ExpectedClientImagePath is string expectedPath &&
            !string.Equals(
                System.IO.Path.GetFullPath(imagePath),
                System.IO.Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refused a connection from an unexpected image.");
            return false;
        }

        if (_options.ExpectedClientCertificateSubject is string expectedSubject &&
            !HasExpectedSignature(imagePath, expectedSubject))
        {
            _logger.LogWarning("Refused a connection from an image with an unexpected signature.");
            return false;
        }

        return true;
    }

    private string? ResolveClientImagePath(NamedPipeServerStream pipe)
    {
        try
        {
            if (!NativeGetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint processId))
            {
                return null;
            }

            using Process process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not resolve the calling process image.");
            return null;
        }
    }

    private bool HasExpectedSignature(string imagePath, string expectedSubject)
    {
        try
        {
            // The Authenticode certificate is embedded in the image, so the file itself is the
            // certificate store to load from.
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(imagePath);
            return string.Equals(certificate.Subject, expectedSubject, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                       or System.IO.IOException)
        {
            _logger.LogWarning(ex, "The calling image is unsigned or its signature could not be read.");
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
