using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.State;

namespace Velocity.Core.Processes;

/// <summary>
/// Exposes per process scheduling state to the snapshot and rollback engines.
/// </summary>
/// <remarks>
/// <para>
/// Keys are <c>process://&lt;executable name&gt;#priority</c> and
/// <c>process://&lt;executable name&gt;#affinity</c>. A value is a JSON map from process id to the
/// setting, because a name can match several running processes and each one's original value has to
/// come back to <em>that</em> process.
/// </para>
/// <para>
/// Process identifiers do not survive a reboot, which is exactly why the modules built on this
/// provider are session scoped. On restore, a process that has exited is skipped: it took its
/// modified state with it.
/// </para>
/// <para>
/// A protected process is never written. The check lives here, at the provider, rather than in each
/// module, so a future module cannot reach a security process by forgetting to ask.
/// </para>
/// </remarks>
public sealed class ProcessStateProvider : IStateProvider
{
    /// <summary>Scheme this provider answers for.</summary>
    public const string Scheme = "process";

    /// <summary>Item name for a process's priority class.</summary>
    public const string PriorityItem = "priority";

    /// <summary>Item name for a process's affinity mask.</summary>
    public const string AffinityItem = "affinity";

    private readonly IProcessInspector _inspector;
    private readonly IProcessController _controller;
    private readonly ILogger<ProcessStateProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="inspector">Reads the process list.</param>
    /// <param name="controller">Changes process scheduling state.</param>
    /// <param name="logger">Logger.</param>
    public ProcessStateProvider(
        IProcessInspector inspector,
        IProcessController controller,
        ILogger<ProcessStateProvider> logger)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => Scheme;

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public bool RequiresElevation(StateKey key) => false;

    /// <inheritdoc />
    public async Task<StateValue> ReadAsync(StateKey key, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessSnapshot> matches =
            await FindAsync(key.Path, cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return StateValue.Absent;
        }

        Dictionary<string, string> values = key.Item switch
        {
            PriorityItem => matches.ToDictionary(
                process => process.ProcessId.ToString(CultureInfo.InvariantCulture),
                process => process.Priority.ToString()),
            AffinityItem => matches
                .Where(process => process.AffinityMask is not null)
                .ToDictionary(
                    process => process.ProcessId.ToString(CultureInfo.InvariantCulture),
                    process => process.AffinityMask!.Value.ToString(CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException($"'{key.Item}' is not a process state item.", nameof(key)),
        };

        return values.Count == 0
            ? StateValue.Absent
            : new StateValue(StateValueKind.Json, JsonSerializer.Serialize(values));
    }

    /// <inheritdoc />
    public async Task WriteAsync(StateKey key, StateValue value, CancellationToken cancellationToken)
    {
        if (value.IsAbsent)
        {
            // Absent means "these processes were not running when we looked", so there is nothing
            // to restore. Silently succeeding is correct here.
            return;
        }

        Dictionary<string, string>? values =
            JsonSerializer.Deserialize<Dictionary<string, string>>(value.Data ?? "{}");

        if (values is null || values.Count == 0)
        {
            return;
        }

        IReadOnlyList<ProcessSnapshot> running =
            await FindAsync(key.Path, cancellationToken).ConfigureAwait(false);
        var runningIds = running.ToDictionary(process => process.ProcessId);

        foreach (KeyValuePair<string, string> entry in values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ||
                !runningIds.TryGetValue(processId, out ProcessSnapshot? process))
            {
                // The process exited; it took the modification with it.
                continue;
            }

            if (process.Protection == ProcessProtection.Protected)
            {
                _logger.LogDebug(
                    "Refusing to write {Item} on protected process {Process}.", key.Item, process.ExecutableName);
                continue;
            }

            await ApplyAsync(key, process, entry.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(
        StateKey key,
        ProcessSnapshot process,
        string rawValue,
        CancellationToken cancellationToken)
    {
        switch (key.Item)
        {
            case PriorityItem when Enum.TryParse(rawValue, out ProcessPriority priority):
                await _controller
                    .SetPriorityAsync(process.ProcessId, priority, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case AffinityItem when ulong.TryParse(
                rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong mask):
                await _controller
                    .SetAffinityAsync(process.ProcessId, mask, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning(
                    "Ignoring unrecognised process state {Item}={Value}.", key.Item, rawValue);
                break;
        }
    }

    private async Task<IReadOnlyList<ProcessSnapshot>> FindAsync(
        string executableName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessSnapshot> processes =
            await _inspector.GetProcessesAsync(cancellationToken).ConfigureAwait(false);

        return processes
            .Where(process => string.Equals(
                process.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(process => process.ProcessId)
            .ToList();
    }
}
