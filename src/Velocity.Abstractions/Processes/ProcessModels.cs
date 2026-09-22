using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Velocity.Abstractions.Processes;

/// <summary>Windows process priority classes, in scheduler order.</summary>
public enum ProcessPriority
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>Idle: runs only when nothing else wants the processor.</summary>
    Idle = 1,

    /// <summary>Below normal.</summary>
    BelowNormal = 2,

    /// <summary>Normal, the default for a started process.</summary>
    Normal = 3,

    /// <summary>Above normal.</summary>
    AboveNormal = 4,

    /// <summary>High. Above every ordinary process but below real time.</summary>
    High = 5,

    /// <summary>
    /// Real time. Never set by this product: a real time process can starve the input stack and
    /// the audio engine, which is the opposite of what a gamer wants.
    /// </summary>
    RealTime = 6,
}

/// <summary>How much this product is willing to interfere with a process.</summary>
public enum ProcessProtection
{
    /// <summary>Ordinary user software; may be de-prioritised, confined or suspended.</summary>
    None = 0,

    /// <summary>
    /// May be de-prioritised or confined, but never suspended: suspension would break something
    /// the user notices, such as audio or input.
    /// </summary>
    NeverSuspend = 1,

    /// <summary>
    /// Never touched at all. Security software, the session manager, the shell and anything whose
    /// interruption could cost the user data or protection.
    /// </summary>
    Protected = 2,
}

/// <summary>One running process, as the optimizer sees it.</summary>
public sealed record ProcessSnapshot
{
    /// <summary>Process identifier.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Executable name without a path, for example <c>chrome.exe</c>.</summary>
    public required string ExecutableName { get; init; }

    /// <summary>Full image path, when it can be read.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Authenticode subject of the image, when it is signed and readable.</summary>
    public string? Publisher { get; init; }

    /// <summary>Current priority class.</summary>
    public ProcessPriority Priority { get; init; } = ProcessPriority.Unknown;

    /// <summary>Group relative processor affinity mask, when it can be read.</summary>
    public ulong? AffinityMask { get; init; }

    /// <summary>Private working set in bytes.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>Total processor time consumed since the process started.</summary>
    public TimeSpan TotalProcessorTime { get; init; }

    /// <summary>Number of threads.</summary>
    public int ThreadCount { get; init; }

    /// <summary><see langword="true"/> when the process owns the foreground window.</summary>
    public bool IsForeground { get; init; }

    /// <summary><see langword="true"/> when the process runs as a service or a system account.</summary>
    public bool IsSystemProcess { get; init; }

    /// <summary>How far this product may go with this process.</summary>
    public ProcessProtection Protection { get; init; } = ProcessProtection.None;
}

/// <summary>Reads the running process list.</summary>
public interface IProcessInspector
{
    /// <summary>Enumerates the running processes the current user can see.</summary>
    /// <param name="cancellationToken">Token used to abort the enumeration.</param>
    /// <returns>The processes.</returns>
    Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Reads one process.</summary>
    /// <param name="processId">Process identifier.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The process, or <see langword="null"/> when it has exited.</returns>
    Task<ProcessSnapshot?> GetProcessAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>Changes how a process is scheduled.</summary>
/// <remarks>
/// Every operation here is scoped to the lifetime of the process, so nothing it does outlives a
/// reboot. That is why the modules built on it are session scoped and why their rollback restores
/// the previous value only for processes that are still running.
/// </remarks>
public interface IProcessController
{
    /// <summary>Sets a process's priority class.</summary>
    /// <param name="processId">Process identifier.</param>
    /// <param name="priority">Priority to set.</param>
    /// <param name="cancellationToken">Token used to abort the change.</param>
    /// <returns><see langword="true"/> when the change was made.</returns>
    Task<bool> SetPriorityAsync(int processId, ProcessPriority priority, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a process's CPU sets: a hint that the scheduler may place threads on these processors.
    /// </summary>
    /// <param name="processId">Process identifier.</param>
    /// <param name="logicalProcessorIndexes">Global indexes of the permitted processors.</param>
    /// <param name="cancellationToken">Token used to abort the change.</param>
    /// <returns><see langword="true"/> when the change was made.</returns>
    /// <remarks>
    /// CPU sets are preferred over a hard affinity mask for a game: they express a preference the
    /// scheduler can override under load, so a badly chosen set degrades performance instead of
    /// deadlocking a thread pool.
    /// </remarks>
    Task<bool> SetCpuSetsAsync(
        int processId,
        IReadOnlyList<int> logicalProcessorIndexes,
        CancellationToken cancellationToken);

    /// <summary>Clears a process's CPU sets, returning it to the default placement.</summary>
    /// <param name="processId">Process identifier.</param>
    /// <param name="cancellationToken">Token used to abort the change.</param>
    /// <returns><see langword="true"/> when the change was made.</returns>
    Task<bool> ClearCpuSetsAsync(int processId, CancellationToken cancellationToken);

    /// <summary>Sets a hard processor affinity mask.</summary>
    /// <param name="processId">Process identifier.</param>
    /// <param name="affinityMask">Group relative mask.</param>
    /// <param name="cancellationToken">Token used to abort the change.</param>
    /// <returns><see langword="true"/> when the change was made.</returns>
    /// <remarks>
    /// Used for background processes, never for a game: a hard mask the scheduler cannot override
    /// is the right tool for confining something that must stay out of the way, and the wrong tool
    /// for something that must run fast.
    /// </remarks>
    Task<bool> SetAffinityAsync(int processId, ulong affinityMask, CancellationToken cancellationToken);
}
