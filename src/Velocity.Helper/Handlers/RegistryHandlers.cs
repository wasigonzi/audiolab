using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.State;
using Velocity.Ipc;
using Velocity.Ipc.Authorization;
using Velocity.Ipc.Protocol;
using Velocity.Platform.Windows.State;

namespace Velocity.Helper.Handlers;

/// <summary>Reads a registry value on behalf of the desktop application.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryReadHandler : IIpcRequestHandler
{
    private readonly ILogger<RegistryReadHandler> _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="logger">Logger.</param>
    public RegistryReadHandler(ILogger<RegistryReadHandler> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string Operation => IpcOperations.RegistryRead;

    /// <inheritdoc />
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (!request.Arguments.TryGetValue("hive", out string? hive) ||
            !request.Arguments.TryGetValue("path", out string? path))
        {
            return Task.FromResult(IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.InvalidArguments, "hive and path are required."));
        }

        request.Arguments.TryGetValue("name", out string? valueName);

        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryRead(hive, path);
        if (!decision.Allowed)
        {
            _logger.LogWarning("Refused a privileged read: {Reason}", decision.Reason);
            return Task.FromResult(IpcResponse.Fail(request.RequestId, IpcErrorCodes.Forbidden, decision.Reason));
        }

        try
        {
            (StateValueKind kind, string? data) = ReadValue(hive, path, valueName);

            return Task.FromResult(IpcResponse.Ok(
                request.RequestId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = kind.ToString(),
                    ["data"] = data ?? string.Empty,
                }));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Task.FromResult(IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.OperationFailed, ex.Message));
        }
    }

    private static (StateValueKind Kind, string? Data) ReadValue(string hive, string path, string? valueName)
    {
        using RegistryKey? subKey = OpenForRead(hive, path);
        if (subKey is null || valueName is null)
        {
            return (StateValueKind.Absent, null);
        }

        object? value = subKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return (StateValueKind.Absent, null);
        }

        // Reuse the provider's conversion so the helper and the desktop agree byte for byte on
        // what a captured value means.
        StateValue converted = RegistryStateProvider.ToStateValue(subKey.GetValueKind(valueName), value);
        return (converted.Kind, converted.Data);
    }

    private static RegistryKey? OpenForRead(string hive, string path) => hive.ToUpperInvariant() switch
    {
        "HKLM" => Registry.LocalMachine.OpenSubKey(path, writable: false),
        "HKCU" => Registry.CurrentUser.OpenSubKey(path, writable: false),
        "HKCR" => Registry.ClassesRoot.OpenSubKey(path, writable: false),
        "HKU" => Registry.Users.OpenSubKey(path, writable: false),
        _ => null,
    };
}

/// <summary>
/// Writes or deletes a registry value on behalf of the desktop application.
/// </summary>
/// <remarks>
/// This is the one operation that gives the desktop process real power, so it is also the one that
/// is authorized twice: once optimistically on the caller's side for a fast, consistent error
/// message, and again here, where the decision actually counts because the caller cannot influence
/// it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RegistryWriteHandler : IIpcRequestHandler
{
    private readonly ILogger<RegistryWriteHandler> _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="logger">Logger.</param>
    public RegistryWriteHandler(ILogger<RegistryWriteHandler> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string Operation => IpcOperations.RegistryWrite;

    /// <inheritdoc />
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (!request.Arguments.TryGetValue("hive", out string? hive) ||
            !request.Arguments.TryGetValue("path", out string? path) ||
            !request.Arguments.TryGetValue("name", out string? valueName) ||
            !request.Arguments.TryGetValue("kind", out string? kindText))
        {
            return Task.FromResult(IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.InvalidArguments, "hive, path, name and kind are required."));
        }

        if (!Enum.TryParse(kindText, ignoreCase: true, out StateValueKind kind))
        {
            return Task.FromResult(IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.InvalidArguments, $"'{kindText}' is not a known value kind."));
        }

        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizeRegistryWrite(hive, path, valueName);
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "Refused a privileged write to {Hive}\\{Path}!{Value}: {Reason}",
                hive, path, valueName, decision.Reason);

            return Task.FromResult(IpcResponse.Fail(request.RequestId, IpcErrorCodes.Forbidden, decision.Reason));
        }

        request.Arguments.TryGetValue("data", out string? data);

        try
        {
            RegistryStateProvider.WriteDirect(
                hive, path, valueName, new StateValue(kind, kind == StateValueKind.Absent ? null : data));

            _logger.LogInformation(
                "Wrote {Hive}\\{Path}!{Value} ({Kind}) for the desktop application.",
                hive, path, valueName, kind);

            return Task.FromResult(IpcResponse.Ok(request.RequestId));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or FormatException or InvalidOperationException)
        {
            _logger.LogError(ex, "Privileged write to {Hive}\\{Path}!{Value} failed.", hive, path, valueName);
            return Task.FromResult(IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.OperationFailed, ex.Message));
        }
    }
}
