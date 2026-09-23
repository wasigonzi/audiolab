using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Processes;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Processes;

/// <summary>Changes how a process is scheduled.</summary>
/// <remarks>
/// <para>
/// CPU set identifiers are not logical processor indexes. <c>SetProcessDefaultCpuSets</c> takes the
/// identifiers <c>GetSystemCpuSetInformation</c> reports, so this class builds the mapping from
/// (processor group, group relative index) to CPU set id once and caches it. Passing processor
/// indexes directly, as several examples on the internet do, silently sets the wrong cores.
/// </para>
/// <para>
/// Every operation returns a boolean rather than throwing on a process that has exited or refused
/// the change: both are ordinary during a gaming session, and a rollback that threw on the first
/// exited process would abandon the rest.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessController : IProcessController
{
    private readonly ISystemProfileProvider _profileProvider;
    private readonly ILogger<WindowsProcessController> _logger;
    private readonly SemaphoreSlim _cpuSetLock = new(1, 1);

    private IReadOnlyDictionary<(ushort Group, byte Index), uint>? _cpuSetIds;

    /// <summary>Creates the controller.</summary>
    /// <param name="profileProvider">Source of the processor topology used to map indexes.</param>
    /// <param name="logger">Logger.</param>
    public WindowsProcessController(
        ISystemProfileProvider profileProvider,
        ILogger<WindowsProcessController> logger)
    {
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> SetPriorityAsync(
        int processId,
        ProcessPriority priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (priority == ProcessPriority.RealTime)
        {
            // Real time can starve the input and audio stacks. The product never sets it.
            _logger.LogWarning("Refusing to set real time priority on process {ProcessId}.", processId);
            return Task.FromResult(false);
        }

        return Task.FromResult(WithProcess(processId, process =>
        {
            process.PriorityClass = MapPriority(priority);
            return true;
        }));
    }

    /// <inheritdoc />
    public Task<bool> SetAffinityAsync(int processId, ulong affinityMask, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (affinityMask == 0)
        {
            _logger.LogWarning("Refusing an empty affinity mask for process {ProcessId}.", processId);
            return Task.FromResult(false);
        }

        return Task.FromResult(WithProcess(processId, process =>
        {
            process.ProcessorAffinity = (IntPtr)(long)affinityMask;
            return true;
        }));
    }

    /// <inheritdoc />
    public async Task<bool> SetCpuSetsAsync(
        int processId,
        IReadOnlyList<int> logicalProcessorIndexes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logicalProcessorIndexes);

        if (logicalProcessorIndexes.Count == 0)
        {
            return false;
        }

        uint[] ids = await ResolveCpuSetIdsAsync(logicalProcessorIndexes, cancellationToken)
            .ConfigureAwait(false);

        if (ids.Length == 0)
        {
            _logger.LogWarning(
                "None of the requested processors could be mapped to a CPU set id; placement skipped.");
            return false;
        }

        return WithProcess(processId, process =>
        {
            IntPtr buffer = Marshal.AllocHGlobal(ids.Length * sizeof(uint));
            try
            {
                Marshal.Copy(Array.ConvertAll(ids, id => unchecked((int)id)), 0, buffer, ids.Length);

                if (NativeMethods.SetProcessDefaultCpuSets(process.Handle, buffer, (uint)ids.Length))
                {
                    return true;
                }

                _logger.LogWarning(
                    "SetProcessDefaultCpuSets failed for process {ProcessId} with error {Error}.",
                    processId,
                    Marshal.GetLastWin32Error());
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        });
    }

    /// <inheritdoc />
    public Task<bool> ClearCpuSetsAsync(int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(WithProcess(processId, process =>
            NativeMethods.SetProcessDefaultCpuSets(process.Handle, IntPtr.Zero, 0)));
    }

    private async Task<uint[]> ResolveCpuSetIdsAsync(
        IReadOnlyList<int> logicalProcessorIndexes,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<(ushort, byte), uint> map =
            await GetCpuSetMapAsync(cancellationToken).ConfigureAwait(false);

        SystemProfile profile = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var byGlobalIndex = profile.Cpu.LogicalProcessors.ToDictionary(processor => processor.GlobalIndex);

        var ids = new List<uint>(logicalProcessorIndexes.Count);

        foreach (int index in logicalProcessorIndexes)
        {
            if (byGlobalIndex.TryGetValue(index, out LogicalProcessor? processor) &&
                map.TryGetValue((processor.GroupId, processor.GroupRelativeIndex), out uint id))
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    private async Task<IReadOnlyDictionary<(ushort Group, byte Index), uint>> GetCpuSetMapAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<(ushort, byte), uint>? cached = _cpuSetIds;
        if (cached is not null)
        {
            return cached;
        }

        await _cpuSetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cpuSetIds ??= ReadCpuSetMap();
            return _cpuSetIds;
        }
        finally
        {
            _cpuSetLock.Release();
        }
    }

    private Dictionary<(ushort Group, byte Index), uint> ReadCpuSetMap()
    {
        var map = new Dictionary<(ushort, byte), uint>();

        NativeMethods.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint required, IntPtr.Zero, 0);

        if (required == 0)
        {
            _logger.LogWarning("The system reported no CPU set information; placement is unavailable.");
            return map;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!NativeMethods.GetSystemCpuSetInformation(buffer, required, out uint written, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read CPU set information.");
            }

            byte[] managed = new byte[written];
            Marshal.Copy(buffer, managed, 0, (int)written);

            int offset = 0;
            while (offset + NativeMethods.CpuSetEntrySize <= managed.Length)
            {
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(managed.AsSpan(offset));
                if (size <= 0 || offset + size > managed.Length)
                {
                    break;
                }

                uint type = BinaryPrimitives.ReadUInt32LittleEndian(
                    managed.AsSpan(offset + NativeMethods.CpuSetTypeOffset));

                if (type == NativeMethods.CpuSetInformationType)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(
                        managed.AsSpan(offset + NativeMethods.CpuSetIdOffset));
                    ushort group = BinaryPrimitives.ReadUInt16LittleEndian(
                        managed.AsSpan(offset + NativeMethods.CpuSetGroupOffset));
                    byte index = managed[offset + NativeMethods.CpuSetLogicalProcessorIndexOffset];

                    map[(group, index)] = id;
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }

    private bool WithProcess(int processId, Func<Process, bool> action)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return action(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or Win32Exception or NotSupportedException)
        {
            // Exited, or owned by an account this process cannot open. Both are ordinary.
            _logger.LogDebug(ex, "Could not change scheduling state of process {ProcessId}.", processId);
            return false;
        }
    }

    private static ProcessPriorityClass MapPriority(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Idle => ProcessPriorityClass.Idle,
        ProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriority.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };
}
