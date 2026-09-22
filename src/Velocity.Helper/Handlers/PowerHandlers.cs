using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Power;
using Velocity.Ipc;
using Velocity.Ipc.Authorization;
using Velocity.Ipc.Protocol;

namespace Velocity.Helper.Handlers;

/// <summary>Returns the active power scheme on behalf of the desktop application.</summary>
/// <remarks>
/// Reading the active scheme needs no elevation, so this exists only so that the desktop process
/// and the helper agree on which scheme a write targets. Reads are not authorized by the power
/// allow list: describing the machine honestly requires reading settings the product will never
/// change.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PowerGetActiveSchemeHandler : IIpcRequestHandler
{
    private readonly IPowerConfigurationController _controller;

    /// <summary>Creates the handler.</summary>
    /// <param name="controller">Power configuration controller.</param>
    public PowerGetActiveSchemeHandler(IPowerConfigurationController controller) =>
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    /// <inheritdoc />
    public string Operation => IpcOperations.PowerGetActiveScheme;

    /// <inheritdoc />
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid scheme = await _controller.GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false);

        return IpcResponse.Ok(
            request.RequestId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scheme"] = scheme.ToString("D", CultureInfo.InvariantCulture),
            });
    }
}

/// <summary>Activates a power scheme on behalf of the desktop application.</summary>
[SupportedOSPlatform("windows")]
public sealed class PowerSetActiveSchemeHandler : IIpcRequestHandler
{
    private readonly IPowerConfigurationController _controller;
    private readonly ILogger<PowerSetActiveSchemeHandler> _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="controller">Power configuration controller.</param>
    /// <param name="logger">Logger.</param>
    public PowerSetActiveSchemeHandler(
        IPowerConfigurationController controller,
        ILogger<PowerSetActiveSchemeHandler> logger)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Operation => IpcOperations.PowerSetActiveScheme;

    /// <inheritdoc />
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Arguments.TryGetValue("scheme", out string? schemeText);

        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizePowerSchemeActivation(schemeText);
        if (!decision.Allowed)
        {
            _logger.LogWarning("Refused a power scheme activation: {Reason}", decision.Reason);
            return IpcResponse.Fail(request.RequestId, IpcErrorCodes.Forbidden, decision.Reason);
        }

        var scheme = Guid.Parse(schemeText!, CultureInfo.InvariantCulture);
        bool activated = await _controller.SetActiveSchemeAsync(scheme, cancellationToken).ConfigureAwait(false);

        if (!activated)
        {
            return IpcResponse.Fail(
                request.RequestId,
                IpcErrorCodes.OperationFailed,
                $"Windows did not activate scheme {scheme:D}.");
        }

        _logger.LogInformation("Activated power scheme {Scheme} for the desktop application.", scheme);
        return IpcResponse.Ok(request.RequestId);
    }
}

/// <summary>Reads one AC power setting value on behalf of the desktop application.</summary>
[SupportedOSPlatform("windows")]
public sealed class PowerReadAcValueHandler : IIpcRequestHandler
{
    private readonly IPowerConfigurationController _controller;

    /// <summary>Creates the handler.</summary>
    /// <param name="controller">Power configuration controller.</param>
    public PowerReadAcValueHandler(IPowerConfigurationController controller) =>
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    /// <inheritdoc />
    public string Operation => IpcOperations.PowerReadAcValue;

    /// <inheritdoc />
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadTriple(request, out Guid scheme, out Guid subgroup, out Guid setting, out string? error))
        {
            return IpcResponse.Fail(request.RequestId, IpcErrorCodes.InvalidArguments, error!);
        }

        uint? value = await _controller
            .ReadAcValueAsync(scheme, subgroup, setting, cancellationToken)
            .ConfigureAwait(false);

        // "present" distinguishes a setting the platform hides from one whose value is zero. The
        // snapshot layer records the first as absent, and restoring an absent value writes nothing.
        return IpcResponse.Ok(
            request.RequestId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["present"] = (value is not null).ToString(CultureInfo.InvariantCulture),
                ["value"] = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            });
    }

    internal static bool TryReadTriple(
        IpcRequest request,
        out Guid scheme,
        out Guid subgroup,
        out Guid setting,
        out string? error)
    {
        scheme = subgroup = setting = Guid.Empty;
        error = null;

        request.Arguments.TryGetValue("scheme", out string? schemeText);
        request.Arguments.TryGetValue("subgroup", out string? subgroupText);
        request.Arguments.TryGetValue("setting", out string? settingText);

        if (!Guid.TryParse(schemeText, out scheme) ||
            !Guid.TryParse(subgroupText, out subgroup) ||
            !Guid.TryParse(settingText, out setting))
        {
            error = "scheme, subgroup and setting must all be GUIDs.";
            return false;
        }

        return true;
    }
}

/// <summary>Writes one AC power setting value on behalf of the desktop application.</summary>
/// <remarks>
/// The setting is authorized by GUID against the power allow list, here, where the caller cannot
/// influence the decision. The DC side is not reachable through the protocol at all: there is no
/// operation for it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PowerWriteAcValueHandler : IIpcRequestHandler
{
    private readonly IPowerConfigurationController _controller;
    private readonly ILogger<PowerWriteAcValueHandler> _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="controller">Power configuration controller.</param>
    /// <param name="logger">Logger.</param>
    public PowerWriteAcValueHandler(
        IPowerConfigurationController controller,
        ILogger<PowerWriteAcValueHandler> logger)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Operation => IpcOperations.PowerWriteAcValue;

    /// <inheritdoc />
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PowerReadAcValueHandler.TryReadTriple(
                request, out Guid scheme, out Guid subgroup, out Guid setting, out string? error))
        {
            return IpcResponse.Fail(request.RequestId, IpcErrorCodes.InvalidArguments, error!);
        }

        if (!request.Arguments.TryGetValue("value", out string? valueText) ||
            !uint.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
        {
            return IpcResponse.Fail(
                request.RequestId, IpcErrorCodes.InvalidArguments, "value must be a non negative integer.");
        }

        PolicyDecision decision = PrivilegedOperationPolicy.AuthorizePowerSettingWrite(
            subgroup.ToString("D", CultureInfo.InvariantCulture),
            setting.ToString("D", CultureInfo.InvariantCulture));

        if (!decision.Allowed)
        {
            _logger.LogWarning("Refused a power setting write: {Reason}", decision.Reason);
            return IpcResponse.Fail(request.RequestId, IpcErrorCodes.Forbidden, decision.Reason);
        }

        bool written = await _controller
            .WriteAcValueAsync(scheme, subgroup, setting, value, cancellationToken)
            .ConfigureAwait(false);

        if (!written)
        {
            return IpcResponse.Fail(
                request.RequestId,
                IpcErrorCodes.OperationFailed,
                $"Windows refused the write to power setting {setting:D}.");
        }

        _logger.LogInformation(
            "Wrote power setting {Setting} = {Value} in scheme {Scheme} for the desktop application.",
            setting,
            value,
            scheme);

        return IpcResponse.Ok(request.RequestId);
    }
}
