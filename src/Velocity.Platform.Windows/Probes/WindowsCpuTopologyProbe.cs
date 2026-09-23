using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.Hardware;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Probes;

/// <summary>
/// Reads the processor topology through <c>GetLogicalProcessorInformationEx</c>.
/// </summary>
/// <remarks>
/// <para>
/// The returned buffer is a sequence of variable length records, so it is parsed by walking
/// offsets rather than by marshalling fixed structures: several of the relationship payloads end
/// in a variable length <c>GROUP_AFFINITY</c> array, which a fixed struct cannot express. Every
/// offset used below is taken from the documented layout and named in a comment.
/// </para>
/// <para>
/// Nothing here interprets the topology. Whether two cores belong to the same CCD, and which cores
/// a game should prefer, is decided by <c>CpuTopologyAnalyzer</c> in the portable engine, where it
/// can be tested against fixtures from machines the team does not own.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuTopologyProbe : ICpuTopologyProbe
{
    private const string ProcessorRegistryPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    // Record header: Relationship (DWORD) at 0, Size (DWORD) at 4, payload from 8.
    private const int RecordRelationshipOffset = 0;
    private const int RecordSizeOffset = 4;

    // PROCESSOR_RELATIONSHIP, relative to the record start. Flags (LTP_PC_SMT) sits at offset 8 and
    // is not read: the number of group mask bits already tells us how many threads the core hosts,
    // and that count is what affinity decisions need.
    private const int ProcessorEfficiencyClassOffset = 9;
    private const int ProcessorGroupCountOffset = 30;
    private const int ProcessorGroupMaskOffset = 32;

    // CACHE_RELATIONSHIP, relative to the record start.
    private const int CacheLevelOffset = 8;
    private const int CacheAssociativityOffset = 9;
    private const int CacheLineSizeOffset = 10;
    private const int CacheSizeOffset = 12;
    private const int CacheTypeOffset = 16;
    private const int CacheGroupCountOffset = 38;
    private const int CacheGroupMaskOffset = 40;

    // NUMA_NODE_RELATIONSHIP, relative to the record start.
    private const int NumaNodeNumberOffset = 8;
    private const int NumaGroupCountOffset = 30;
    private const int NumaGroupMaskOffset = 32;

    // GROUP_RELATIONSHIP, relative to the record start.
    private const int GroupActiveGroupCountOffset = 10;
    private const int GroupInfoOffset = 32;
    private const int GroupInfoStride = 48;
    private const int GroupInfoMaximumProcessorCountOffset = 0;
    private const int GroupInfoActiveProcessorCountOffset = 1;
    private const int GroupInfoActiveProcessorMaskOffset = 40;

    // GROUP_AFFINITY: Mask (ULONG_PTR) at 0, Group (WORD) at 8. 16 bytes on 64 bit Windows.
    private const int GroupAffinityStride = 16;
    private const int GroupAffinityGroupOffset = 8;

    private readonly ILogger<WindowsCpuTopologyProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsCpuTopologyProbe(ILogger<WindowsCpuTopologyProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "cpu-topology";

    /// <inheritdoc />
    public Task<CpuTopology> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Probe());
    }

    private CpuTopology Probe()
    {
        byte[] buffer = QueryTopologyBuffer();

        List<ProcessorGroup> groups = ParseGroups(buffer);
        Dictionary<ushort, int> groupBaseIndex = BuildGroupBaseIndexes(groups);

        (List<PhysicalCore> cores, List<LogicalProcessor> logicalProcessors, Dictionary<int, byte> classByProcessor) =
            ParseCores(buffer, groupBaseIndex);

        List<NumaNode> numaNodes = ParseNumaNodes(buffer, groupBaseIndex, logicalProcessors);
        List<CacheDescriptor> caches = ParseCaches(buffer, groupBaseIndex);
        Dictionary<int, IReadOnlyList<int>> dies = ParseDies(buffer, groupBaseIndex);

        ApplyNumaNodes(logicalProcessors, cores, numaNodes);

        (CpuVendor vendor, string brand, string? identifier, int baseFrequencyMhz) = ReadProcessorIdentity();

        bool hybrid = classByProcessor.Values.Distinct().Count() > 1;
        bool smt = cores.Exists(core => core.IsSimultaneousMultiThreaded);

        List<PhysicalCore> classifiedCores = cores
            .Select(core => core with
            {
                Class = ClassifyCore(core.EfficiencyClass, classByProcessor.Values, hybrid),
            })
            .ToList();

        return new CpuTopology
        {
            Vendor = vendor,
            BrandString = brand,
            ProcessorIdentifier = identifier,
            PhysicalCoreCount = classifiedCores.Count,
            LogicalProcessorCount = logicalProcessors.Count,
            BaseFrequencyMhz = baseFrequencyMhz,
            LogicalProcessors = logicalProcessors,
            PhysicalCores = classifiedCores,
            Groups = groups,
            NumaNodes = numaNodes,
            Caches = caches,
            Dies = dies,
            IsHybrid = hybrid,
            IsSimultaneousMultiThreadingEnabled = smt,
        };
    }

    private static CoreClass ClassifyCore(byte efficiencyClass, IEnumerable<byte> allClasses, bool hybrid)
    {
        if (!hybrid)
        {
            return CoreClass.Uniform;
        }

        List<byte> distinct = allClasses.Distinct().OrderBy(value => value).ToList();

        if (efficiencyClass == distinct[^1])
        {
            return CoreClass.Performance;
        }

        // Three or more classes means the lowest is a low power cluster, as on Intel parts with
        // efficiency cores on the SoC tile.
        return distinct.Count >= 3 && efficiencyClass == distinct[0]
            ? CoreClass.LowPowerEfficiency
            : CoreClass.Efficiency;
    }

    private byte[] QueryTopologyBuffer()
    {
        uint length = 0;

        if (NativeMethods.GetLogicalProcessorInformationEx(
                NativeMethods.LogicalProcessorRelationship.All, IntPtr.Zero, ref length))
        {
            throw new InvalidOperationException(
                "GetLogicalProcessorInformationEx unexpectedly succeeded with a zero length buffer.");
        }

        int error = Marshal.GetLastWin32Error();
        if (error != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new System.ComponentModel.Win32Exception(
                error, "Could not determine the size of the processor topology buffer.");
        }

        IntPtr native = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(
                    NativeMethods.LogicalProcessorRelationship.All, native, ref length))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "Could not read the processor topology.");
            }

            byte[] buffer = new byte[length];
            Marshal.Copy(native, buffer, 0, (int)length);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private static IEnumerable<(NativeMethods.LogicalProcessorRelationship Relationship, int Offset, int Size)>
        EnumerateRecords(byte[] buffer)
    {
        int offset = 0;

        while (offset + 8 <= buffer.Length)
        {
            var relationship = (NativeMethods.LogicalProcessorRelationship)
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + RecordRelationshipOffset));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + RecordSizeOffset));

            if (size <= 0 || offset + size > buffer.Length)
            {
                yield break;
            }

            yield return (relationship, offset, size);
            offset += size;
        }
    }

    private static List<ProcessorGroup> ParseGroups(byte[] buffer)
    {
        var groups = new List<ProcessorGroup>();

        foreach ((NativeMethods.LogicalProcessorRelationship relationship, int offset, int size) in
                 EnumerateRecords(buffer))
        {
            if (relationship != NativeMethods.LogicalProcessorRelationship.Group)
            {
                continue;
            }

            ushort activeGroupCount =
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + GroupActiveGroupCountOffset));

            for (ushort group = 0; group < activeGroupCount; group++)
            {
                int infoOffset = offset + GroupInfoOffset + (group * GroupInfoStride);
                if (infoOffset + GroupInfoStride > offset + size)
                {
                    break;
                }

                groups.Add(new ProcessorGroup
                {
                    GroupId = group,
                    MaximumProcessorCount = buffer[infoOffset + GroupInfoMaximumProcessorCountOffset],
                    ActiveProcessorCount = buffer[infoOffset + GroupInfoActiveProcessorCountOffset],
                    ActiveProcessorMask = BinaryPrimitives.ReadUInt64LittleEndian(
                        buffer.AsSpan(infoOffset + GroupInfoActiveProcessorMaskOffset)),
                });
            }
        }

        if (groups.Count == 0)
        {
            // Single group machines still report a GROUP record, but never trust that blindly:
            // synthesise one from the process affinity so the rest of the parse has a base index.
            groups.Add(new ProcessorGroup
            {
                GroupId = 0,
                ActiveProcessorCount = (byte)Math.Min(Environment.ProcessorCount, 64),
                MaximumProcessorCount = (byte)Math.Min(Environment.ProcessorCount, 64),
                ActiveProcessorMask = Environment.ProcessorCount >= 64
                    ? ulong.MaxValue
                    : (1UL << Environment.ProcessorCount) - 1,
            });
        }

        return groups;
    }

    private static Dictionary<ushort, int> BuildGroupBaseIndexes(List<ProcessorGroup> groups)
    {
        var baseIndexes = new Dictionary<ushort, int>();
        int running = 0;

        foreach (ProcessorGroup group in groups.OrderBy(group => group.GroupId))
        {
            baseIndexes[group.GroupId] = running;
            running += group.ActiveProcessorCount;
        }

        return baseIndexes;
    }

    private static (List<PhysicalCore> Cores, List<LogicalProcessor> Processors, Dictionary<int, byte> ClassByProcessor)
        ParseCores(byte[] buffer, Dictionary<ushort, int> groupBaseIndex)
    {
        var cores = new List<PhysicalCore>();
        var processors = new List<LogicalProcessor>();
        var classByProcessor = new Dictionary<int, byte>();
        int coreId = 0;

        foreach ((NativeMethods.LogicalProcessorRelationship relationship, int offset, int size) in
                 EnumerateRecords(buffer))
        {
            if (relationship != NativeMethods.LogicalProcessorRelationship.ProcessorCore)
            {
                continue;
            }

            byte efficiencyClass = buffer[offset + ProcessorEfficiencyClassOffset];
            ushort groupCount =
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + ProcessorGroupCountOffset));

            // GroupCount of zero means the legacy single-mask form.
            int masks = Math.Max((int)groupCount, 1);
            var coreProcessors = new List<int>();
            ushort coreGroup = 0;
            ulong coreMask = 0;

            for (int i = 0; i < masks; i++)
            {
                int affinityOffset = offset + ProcessorGroupMaskOffset + (i * GroupAffinityStride);
                if (affinityOffset + GroupAffinityStride > offset + size)
                {
                    break;
                }

                ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(affinityOffset));
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(affinityOffset + GroupAffinityGroupOffset));

                coreGroup = group;
                coreMask = mask;
                int baseIndex = groupBaseIndex.TryGetValue(group, out int value) ? value : 0;

                for (byte bit = 0; bit < 64; bit++)
                {
                    if ((mask & (1UL << bit)) == 0)
                    {
                        continue;
                    }

                    int globalIndex = baseIndex + bit;
                    coreProcessors.Add(globalIndex);
                    classByProcessor[globalIndex] = efficiencyClass;

                    processors.Add(new LogicalProcessor
                    {
                        GroupId = group,
                        GroupRelativeIndex = bit,
                        GlobalIndex = globalIndex,
                        PhysicalCoreId = coreId,
                        NumaNodeId = 0,
                        EfficiencyClass = efficiencyClass,
                    });
                }
            }

            if (coreProcessors.Count == 0)
            {
                continue;
            }

            cores.Add(new PhysicalCore
            {
                CoreId = coreId,
                GroupId = coreGroup,
                LogicalProcessorIndexes = coreProcessors,
                GroupAffinityMask = coreMask,
                EfficiencyClass = efficiencyClass,
                Class = CoreClass.Unknown,
                NumaNodeId = 0,
            });

            coreId++;
        }

        processors.Sort((left, right) => left.GlobalIndex.CompareTo(right.GlobalIndex));
        return (cores, processors, classByProcessor);
    }

    private static List<NumaNode> ParseNumaNodes(
        byte[] buffer,
        Dictionary<ushort, int> groupBaseIndex,
        List<LogicalProcessor> processors)
    {
        var nodes = new List<NumaNode>();

        foreach ((NativeMethods.LogicalProcessorRelationship relationship, int offset, int size) in
                 EnumerateRecords(buffer))
        {
            if (relationship != NativeMethods.LogicalProcessorRelationship.NumaNode)
            {
                continue;
            }

            uint nodeNumber = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + NumaNodeNumberOffset));
            ushort groupCount =
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + NumaGroupCountOffset));
            int masks = Math.Max((int)groupCount, 1);

            var indexes = new List<int>();
            ushort firstGroup = 0;

            for (int i = 0; i < masks; i++)
            {
                int affinityOffset = offset + NumaGroupMaskOffset + (i * GroupAffinityStride);
                if (affinityOffset + GroupAffinityStride > offset + size)
                {
                    break;
                }

                ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(affinityOffset));
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(affinityOffset + GroupAffinityGroupOffset));

                if (i == 0)
                {
                    firstGroup = group;
                }

                indexes.AddRange(ExpandMask(mask, group, groupBaseIndex));
            }

            nodes.Add(new NumaNode
            {
                NodeId = nodeNumber,
                GroupId = firstGroup,
                LogicalProcessorIndexes = indexes,
            });
        }

        if (nodes.Count == 0 && processors.Count > 0)
        {
            nodes.Add(new NumaNode
            {
                NodeId = 0,
                GroupId = 0,
                LogicalProcessorIndexes = processors.Select(processor => processor.GlobalIndex).ToList(),
            });
        }

        return nodes;
    }

    private static List<CacheDescriptor> ParseCaches(byte[] buffer, Dictionary<ushort, int> groupBaseIndex)
    {
        var caches = new List<CacheDescriptor>();

        foreach ((NativeMethods.LogicalProcessorRelationship relationship, int offset, int size) in
                 EnumerateRecords(buffer))
        {
            if (relationship != NativeMethods.LogicalProcessorRelationship.Cache)
            {
                continue;
            }

            byte level = buffer[offset + CacheLevelOffset];
            byte associativity = buffer[offset + CacheAssociativityOffset];
            ushort lineSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + CacheLineSizeOffset));
            uint cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + CacheSizeOffset));
            var type = (NativeMethods.ProcessorCacheType)
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + CacheTypeOffset));
            ushort groupCount =
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + CacheGroupCountOffset));
            int masks = Math.Max((int)groupCount, 1);

            var indexes = new List<int>();
            ushort firstGroup = 0;

            for (int i = 0; i < masks; i++)
            {
                int affinityOffset = offset + CacheGroupMaskOffset + (i * GroupAffinityStride);
                if (affinityOffset + GroupAffinityStride > offset + size)
                {
                    break;
                }

                ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(affinityOffset));
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(affinityOffset + GroupAffinityGroupOffset));

                if (i == 0)
                {
                    firstGroup = group;
                }

                indexes.AddRange(ExpandMask(mask, group, groupBaseIndex));
            }

            caches.Add(new CacheDescriptor
            {
                Level = level,
                Kind = type switch
                {
                    NativeMethods.ProcessorCacheType.Instruction => CacheKind.Instruction,
                    NativeMethods.ProcessorCacheType.Data => CacheKind.Data,
                    NativeMethods.ProcessorCacheType.Trace => CacheKind.Trace,
                    _ => CacheKind.Unified,
                },
                SizeBytes = cacheSize,
                LineSizeBytes = lineSize,
                Associativity = associativity,
                GroupId = firstGroup,
                SharedByLogicalProcessorIndexes = indexes,
            });
        }

        return caches;
    }

    private static Dictionary<int, IReadOnlyList<int>> ParseDies(
        byte[] buffer,
        Dictionary<ushort, int> groupBaseIndex)
    {
        var dies = new Dictionary<int, IReadOnlyList<int>>();
        int dieId = 0;

        foreach ((NativeMethods.LogicalProcessorRelationship relationship, int offset, int size) in
                 EnumerateRecords(buffer))
        {
            if (relationship != NativeMethods.LogicalProcessorRelationship.ProcessorDie)
            {
                continue;
            }

            ushort groupCount =
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + ProcessorGroupCountOffset));
            int masks = Math.Max((int)groupCount, 1);
            var indexes = new List<int>();

            for (int i = 0; i < masks; i++)
            {
                int affinityOffset = offset + ProcessorGroupMaskOffset + (i * GroupAffinityStride);
                if (affinityOffset + GroupAffinityStride > offset + size)
                {
                    break;
                }

                ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(affinityOffset));
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(affinityOffset + GroupAffinityGroupOffset));

                indexes.AddRange(ExpandMask(mask, group, groupBaseIndex));
            }

            if (indexes.Count > 0)
            {
                dies[dieId++] = indexes;
            }
        }

        return dies;
    }

    private static void ApplyNumaNodes(
        List<LogicalProcessor> processors,
        List<PhysicalCore> cores,
        List<NumaNode> nodes)
    {
        var nodeByProcessor = new Dictionary<int, uint>();
        foreach (NumaNode node in nodes)
        {
            foreach (int index in node.LogicalProcessorIndexes)
            {
                nodeByProcessor[index] = node.NodeId;
            }
        }

        for (int i = 0; i < processors.Count; i++)
        {
            if (nodeByProcessor.TryGetValue(processors[i].GlobalIndex, out uint nodeId))
            {
                processors[i] = processors[i] with { NumaNodeId = nodeId };
            }
        }

        for (int i = 0; i < cores.Count; i++)
        {
            int first = cores[i].LogicalProcessorIndexes.Count > 0 ? cores[i].LogicalProcessorIndexes[0] : -1;
            if (first >= 0 && nodeByProcessor.TryGetValue(first, out uint nodeId))
            {
                cores[i] = cores[i] with { NumaNodeId = nodeId };
            }
        }
    }

    private static IEnumerable<int> ExpandMask(ulong mask, ushort group, Dictionary<ushort, int> groupBaseIndex)
    {
        int baseIndex = groupBaseIndex.TryGetValue(group, out int value) ? value : 0;

        for (byte bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
            {
                yield return baseIndex + bit;
            }
        }
    }

    private (CpuVendor Vendor, string Brand, string? Identifier, int BaseFrequencyMhz) ReadProcessorIdentity()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ProcessorRegistryPath);

            string brand = key?.GetValue("ProcessorNameString") as string ?? "Unknown processor";
            string? vendorId = key?.GetValue("VendorIdentifier") as string;
            string? identifier = key?.GetValue("Identifier") as string;
            int megahertz = key?.GetValue("~MHz") is int frequency ? frequency : 0;

            return (ResolveVendor(vendorId), brand.Trim(), identifier, megahertz);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the processor identity from the registry.");
            return (CpuVendor.Unknown, "Unknown processor", null, 0);
        }
    }

    private static CpuVendor ResolveVendor(string? vendorIdentifier) => vendorIdentifier switch
    {
        null => CpuVendor.Unknown,
        _ when vendorIdentifier.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase) => CpuVendor.Intel,
        _ when vendorIdentifier.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase) => CpuVendor.Amd,
        _ when vendorIdentifier.Contains("ARM", StringComparison.OrdinalIgnoreCase) => CpuVendor.Arm,
        _ when vendorIdentifier.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) => CpuVendor.Arm,
        _ => CpuVendor.Other,
    };
}
