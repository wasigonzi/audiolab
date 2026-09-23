using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Processes;
using Velocity.Core.Processes;

namespace Velocity.TestSupport;

/// <summary>Builds process snapshots for tests without touching the real process list.</summary>
public static class ProcessFixtures
{
    /// <summary>Creates a process snapshot, classified the way the real inspector would.</summary>
    /// <param name="executableName">Executable name.</param>
    /// <param name="processId">Process identifier.</param>
    /// <param name="priority">Priority class.</param>
    /// <param name="isForeground">Whether it owns the foreground window.</param>
    /// <param name="isSystemProcess">Whether it runs under a system account.</param>
    /// <param name="affinityMask">Current affinity mask.</param>
    /// <returns>The snapshot.</returns>
    public static ProcessSnapshot Process(
        string executableName,
        int processId,
        ProcessPriority priority = ProcessPriority.Normal,
        bool isForeground = false,
        bool isSystemProcess = false,
        ulong? affinityMask = null) =>
        ProcessProtectionClassifier.Classify(new ProcessSnapshot
        {
            ProcessId = processId,
            ExecutableName = executableName,
            Priority = priority,
            IsForeground = isForeground,
            IsSystemProcess = isSystemProcess,
            AffinityMask = affinityMask,
            WorkingSetBytes = 64L * 1024 * 1024,
            ThreadCount = 8,
        });
}

/// <summary>A process inspector over a fixed list.</summary>
public sealed class FakeProcessInspector : IProcessInspector
{
    private readonly List<ProcessSnapshot> _processes;

    /// <summary>Creates the inspector.</summary>
    /// <param name="processes">Processes to report.</param>
    public FakeProcessInspector(params ProcessSnapshot[] processes) => _processes = processes.ToList();

    /// <summary>The processes currently reported, mutable so a test can simulate exits.</summary>
    public IList<ProcessSnapshot> Processes => _processes;

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProcessSnapshot>>(_processes.ToList());

    /// <inheritdoc />
    public Task<ProcessSnapshot?> GetProcessAsync(int processId, CancellationToken cancellationToken) =>
        Task.FromResult(_processes.FirstOrDefault(process => process.ProcessId == processId));

    /// <summary>Replaces a process's priority, as the real controller's effect would.</summary>
    /// <param name="processId">Process to change.</param>
    /// <param name="priority">New priority.</param>
    public void SetPriority(int processId, ProcessPriority priority)
    {
        for (int i = 0; i < _processes.Count; i++)
        {
            if (_processes[i].ProcessId == processId)
            {
                _processes[i] = _processes[i] with { Priority = priority };
            }
        }
    }

    /// <summary>Replaces a process's affinity mask.</summary>
    /// <param name="processId">Process to change.</param>
    /// <param name="affinityMask">New mask.</param>
    public void SetAffinity(int processId, ulong affinityMask)
    {
        for (int i = 0; i < _processes.Count; i++)
        {
            if (_processes[i].ProcessId == processId)
            {
                _processes[i] = _processes[i] with { AffinityMask = affinityMask };
            }
        }
    }
}

/// <summary>
/// A process controller that records calls and reflects them back into the inspector, so a test
/// exercises the real read-modify-verify loop rather than a one way write.
/// </summary>
public sealed class FakeProcessController : IProcessController
{
    private readonly FakeProcessInspector _inspector;

    /// <summary>Creates the controller.</summary>
    /// <param name="inspector">Inspector whose state the controller mutates.</param>
    public FakeProcessController(FakeProcessInspector inspector) =>
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <summary>Priority changes, in order.</summary>
    public List<(int ProcessId, ProcessPriority Priority)> PriorityCalls { get; } = new();

    /// <summary>Affinity changes, in order.</summary>
    public List<(int ProcessId, ulong Mask)> AffinityCalls { get; } = new();

    /// <summary>CPU set assignments, in order.</summary>
    public List<(int ProcessId, IReadOnlyList<int> Processors)> CpuSetCalls { get; } = new();

    /// <summary>Processes whose CPU sets were cleared.</summary>
    public List<int> ClearedCpuSets { get; } = new();

    /// <summary>Process ids the controller should report failure for.</summary>
    public HashSet<int> FailingProcessIds { get; } = new();

    /// <inheritdoc />
    public Task<bool> SetPriorityAsync(
        int processId,
        ProcessPriority priority,
        CancellationToken cancellationToken)
    {
        if (FailingProcessIds.Contains(processId))
        {
            return Task.FromResult(false);
        }

        PriorityCalls.Add((processId, priority));
        _inspector.SetPriority(processId, priority);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> SetCpuSetsAsync(
        int processId,
        IReadOnlyList<int> logicalProcessorIndexes,
        CancellationToken cancellationToken)
    {
        if (FailingProcessIds.Contains(processId))
        {
            return Task.FromResult(false);
        }

        CpuSetCalls.Add((processId, logicalProcessorIndexes));
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> ClearCpuSetsAsync(int processId, CancellationToken cancellationToken)
    {
        ClearedCpuSets.Add(processId);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> SetAffinityAsync(int processId, ulong affinityMask, CancellationToken cancellationToken)
    {
        if (FailingProcessIds.Contains(processId))
        {
            return Task.FromResult(false);
        }

        AffinityCalls.Add((processId, affinityMask));
        _inspector.SetAffinity(processId, affinityMask);
        return Task.FromResult(true);
    }
}
