using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Processes;
using Velocity.Core.Processes;

namespace Velocity.Platform.Windows.Processes;

/// <summary>Reads the running process list.</summary>
/// <remarks>
/// <para>
/// Several properties of a process owned by another account, or by a higher integrity level, throw
/// rather than returning a value. Each one is read defensively: a process whose path cannot be read
/// still appears in the list with what is known, because leaving it out would hide it from the
/// protection classifier as well as from the user.
/// </para>
/// <para>
/// Classification is applied here so that no caller can obtain an unclassified snapshot and act on
/// it by mistake.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessInspector : IProcessInspector
{
    private readonly ILogger<WindowsProcessInspector> _logger;

    /// <summary>Creates the inspector.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsProcessInspector(ILogger<WindowsProcessInspector> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        int foregroundProcessId = GetForegroundProcessId();
        var snapshots = new List<ProcessSnapshot>();

        foreach (Process process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                snapshots.Add(Describe(process, foregroundProcessId));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The process exited between enumeration and inspection.
                _logger.LogTrace(ex, "A process disappeared while it was being inspected.");
            }
            finally
            {
                process.Dispose();
            }
        }

        return Task.FromResult<IReadOnlyList<ProcessSnapshot>>(snapshots);
    }

    /// <inheritdoc />
    public Task<ProcessSnapshot?> GetProcessAsync(int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using Process process = Process.GetProcessById(processId);
            return Task.FromResult<ProcessSnapshot?>(Describe(process, GetForegroundProcessId()));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _logger.LogTrace(ex, "Process {ProcessId} is not running.", processId);
            return Task.FromResult<ProcessSnapshot?>(null);
        }
    }

    private ProcessSnapshot Describe(Process process, int foregroundProcessId)
    {
        string executableName = process.ProcessName + ".exe";
        string? path = TryRead(() => process.MainModule?.FileName);
        bool isSystem = TryRead(() => (int?)process.SessionId) == 0;

        return ProcessProtectionClassifier.Classify(new ProcessSnapshot
        {
            ProcessId = process.Id,
            ExecutableName = executableName,
            ExecutablePath = path,
            Priority = MapPriority(TryRead(() => (ProcessPriorityClass?)process.PriorityClass)),
            AffinityMask = TryRead(() => (ulong?)(long)process.ProcessorAffinity),
            WorkingSetBytes = TryRead(() => (long?)process.WorkingSet64) ?? 0L,
            TotalProcessorTime = TryRead(() => (TimeSpan?)process.TotalProcessorTime) ?? TimeSpan.Zero,
            ThreadCount = TryRead(() => (int?)process.Threads.Count) ?? 0,
            IsForeground = process.Id == foregroundProcessId,
            IsSystemProcess = isSystem,
        });
    }

    private T? TryRead<T>(Func<T?> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? TryRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or NotSupportedException or UnauthorizedAccessException)
        {
            // Reading the image path of a process at a higher integrity level is denied; that is
            // expected and not worth logging per process.
            return null;
        }
    }

    private static ProcessPriority MapPriority(ProcessPriorityClass? priorityClass) => priorityClass switch
    {
        ProcessPriorityClass.Idle => ProcessPriority.Idle,
        ProcessPriorityClass.BelowNormal => ProcessPriority.BelowNormal,
        ProcessPriorityClass.Normal => ProcessPriority.Normal,
        ProcessPriorityClass.AboveNormal => ProcessPriority.AboveNormal,
        ProcessPriorityClass.High => ProcessPriority.High,
        ProcessPriorityClass.RealTime => ProcessPriority.RealTime,
        _ => ProcessPriority.Unknown,
    };

    private static int GetForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();

        if (window == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return (int)processId;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
