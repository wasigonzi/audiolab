using System;
using System.Runtime.InteropServices;

namespace Velocity.Platform.Windows.Interop;

/// <summary>
/// Documented Windows entry points used by the platform layer.
/// </summary>
/// <remarks>
/// Only documented, publicly supported APIs appear here. There are no undocumented syscalls and no
/// <c>NtQuerySystemInformation</c> structure guessing: an optimizer that breaks on the next
/// Windows update is worse than one that reports a subsystem as unavailable.
/// </remarks>
internal static partial class NativeMethods
{
    internal const string Kernel32 = "kernel32.dll";
    internal const string PowrProf = "powrprof.dll";
    internal const string User32 = "user32.dll";
    internal const string Psapi = "psapi.dll";

    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorSuccess = 0;
    internal const int ErrorMoreData = 234;

    /// <summary>Relationship types accepted by <see cref="GetLogicalProcessorInformationEx"/>.</summary>
    internal enum LogicalProcessorRelationship : uint
    {
        ProcessorCore = 0,
        NumaNode = 1,
        Cache = 2,
        ProcessorPackage = 3,
        Group = 4,
        ProcessorDie = 5,
        NumaNodeEx = 6,
        ProcessorModule = 7,
        All = 0xffff,
    }

    /// <summary>Cache types reported in <c>CACHE_RELATIONSHIP.Type</c>.</summary>
    internal enum ProcessorCacheType : uint
    {
        Unified = 0,
        Instruction = 1,
        Data = 2,
        Trace = 3,
    }

    /// <summary>
    /// Retrieves processor topology. Called first with a null buffer to learn the required size.
    /// </summary>
    /// <param name="relationshipType">Relationship filter.</param>
    /// <param name="buffer">Destination buffer, or <see cref="IntPtr.Zero"/> to query the size.</param>
    /// <param name="returnedLength">Buffer size in bytes, in and out.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport(Kernel32, EntryPoint = "GetLogicalProcessorInformationEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformationEx(
        LogicalProcessorRelationship relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    /// <summary>Number of active processor groups.</summary>
    /// <returns>The group count.</returns>
    [LibraryImport(Kernel32, EntryPoint = "GetActiveProcessorGroupCount")]
    internal static partial ushort GetActiveProcessorGroupCount();

    /// <summary>Memory status, used for installed and available physical memory.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    /// <summary>Fills a <see cref="MemoryStatusEx"/>.</summary>
    /// <param name="buffer">Structure to fill; <c>Length</c> must be set by the caller.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport(Kernel32, EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>System wide performance information, used for commit charge and page size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerformanceInformation
    {
        internal uint Size;
        internal nuint CommitTotal;
        internal nuint CommitLimit;
        internal nuint CommitPeak;
        internal nuint PhysicalTotal;
        internal nuint PhysicalAvailable;
        internal nuint SystemCache;
        internal nuint KernelTotal;
        internal nuint KernelPaged;
        internal nuint KernelNonpaged;
        internal nuint PageSize;
        internal uint HandleCount;
        internal uint ProcessCount;
        internal uint ThreadCount;
    }

    /// <summary>Fills a <see cref="PerformanceInformation"/>.</summary>
    /// <param name="buffer">Structure to fill.</param>
    /// <param name="size">Size of <paramref name="buffer"/> in bytes.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport(Psapi, EntryPoint = "GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPerformanceInfo(ref PerformanceInformation buffer, uint size);

    /// <summary>Returns the GUID of the active power scheme.</summary>
    /// <param name="userRootPowerKey">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="activePolicyGuid">Receives a pointer to the scheme GUID.</param>
    /// <returns><see cref="ErrorSuccess"/> on success, otherwise a Win32 error code.</returns>
    [LibraryImport(PowrProf, EntryPoint = "PowerGetActiveScheme")]
    internal static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    /// <summary>Reads the friendly name of a power scheme, subgroup or setting.</summary>
    /// <param name="rootPowerKey">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="schemeGuid">Scheme to read.</param>
    /// <param name="subGroupOfPowerSettingsGuid">Subgroup, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="powerSettingGuid">Setting, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="bufferSize">Buffer size in bytes, in and out.</param>
    /// <returns><see cref="ErrorSuccess"/> on success, otherwise a Win32 error code.</returns>
    [LibraryImport(PowrProf, EntryPoint = "PowerReadFriendlyName")]
    internal static partial uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        in Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        IntPtr buffer,
        ref uint bufferSize);

    /// <summary>Enumerates power schemes.</summary>
    /// <param name="rootPowerKey">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="schemeGuid">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="subGroupOfPowerSettingsGuid">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="accessFlags">Level to enumerate; 16 enumerates schemes.</param>
    /// <param name="index">Zero based index of the item to return.</param>
    /// <param name="buffer">Destination buffer receiving a GUID.</param>
    /// <param name="bufferSize">Buffer size in bytes, in and out.</param>
    /// <returns><see cref="ErrorSuccess"/> on success, otherwise a Win32 error code.</returns>
    [LibraryImport(PowrProf, EntryPoint = "PowerEnumerate")]
    internal static partial uint PowerEnumerate(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        IntPtr buffer,
        ref uint bufferSize);

    /// <summary>Reads an AC power setting value index.</summary>
    /// <param name="rootPowerKey">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="schemeGuid">Scheme to read.</param>
    /// <param name="subGroupOfPowerSettingsGuid">Setting subgroup.</param>
    /// <param name="powerSettingGuid">Setting.</param>
    /// <param name="value">Receives the value index.</param>
    /// <returns><see cref="ErrorSuccess"/> on success, otherwise a Win32 error code.</returns>
    [LibraryImport(PowrProf, EntryPoint = "PowerReadACValueIndex")]
    internal static partial uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupOfPowerSettingsGuid,
        in Guid powerSettingGuid,
        out uint value);

    /// <summary>Writes an AC power setting value index.</summary>
    /// <param name="rootPowerKey">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="schemeGuid">Scheme to write to.</param>
    /// <param name="subGroupOfPowerSettingsGuid">Setting subgroup.</param>
    /// <param name="powerSettingGuid">Setting.</param>
    /// <param name="value">Value index to write.</param>
    /// <returns><see cref="ErrorSuccess"/> on success, otherwise a Win32 error code.</returns>
    [LibraryImport(PowrProf, EntryPoint = "PowerWriteACValueIndex")]
    internal static partial uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupOfPowerSettingsGuid,
        in Guid powerSettingGuid,
        uint value);

    /// <summary>Makes a power scheme the active one.</summary>
    /// <param name="userRootPowerKey">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
    /// <param name="schemeGuid">Scheme to activate.</param>
    /// <returns><see cref="ErrorSuccess"/> on success, otherwise a Win32 error code.</returns>
    [LibraryImport(PowrProf, EntryPoint = "PowerSetActiveScheme")]
    internal static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    /// <summary>Frees memory allocated by the power management API.</summary>
    /// <param name="memory">Pointer to free.</param>
    /// <returns>Zero on success.</returns>
    [LibraryImport(Kernel32, EntryPoint = "LocalFree")]
    internal static partial IntPtr LocalFree(IntPtr memory);

    /// <summary>Display device information returned by <see cref="EnumDisplayDevicesW"/>.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDeviceW
    {
        internal uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceString;

        internal uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceKey;
    }

    /// <summary>Display mode information returned by <see cref="EnumDisplaySettingsExW"/>.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        internal ushort SpecVersion;
        internal ushort DriverVersion;
        internal ushort Size;
        internal ushort DriverExtra;
        internal uint Fields;
        internal int PositionX;
        internal int PositionY;
        internal uint DisplayOrientation;
        internal uint DisplayFixedOutput;
        internal short Color;
        internal short Duplex;
        internal short YResolution;
        internal short TrueTypeOption;
        internal short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string FormName;

        internal ushort LogPixels;
        internal uint BitsPerPel;
        internal uint PelsWidth;
        internal uint PelsHeight;
        internal uint DisplayFlags;
        internal uint DisplayFrequency;
        internal uint IcmMethod;
        internal uint IcmIntent;
        internal uint MediaType;
        internal uint DitherType;
        internal uint Reserved1;
        internal uint Reserved2;
        internal uint PanningWidth;
        internal uint PanningHeight;
    }

    internal const int EnumCurrentSettings = -1;
    internal const uint DisplayDeviceAttachedToDesktop = 0x00000001;
    internal const uint DisplayDevicePrimaryDevice = 0x00000004;

    /// <summary>Enumerates display adapters or the monitors attached to one.</summary>
    /// <param name="device">Adapter name, or <see langword="null"/> to enumerate adapters.</param>
    /// <param name="deviceIndex">Zero based index.</param>
    /// <param name="displayDevice">Receives the device information.</param>
    /// <param name="flags">Enumeration flags.</param>
    /// <returns><see langword="true"/> while a device exists at the index.</returns>
    [DllImport(User32, EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevicesW(
        string? device,
        uint deviceIndex,
        ref DisplayDeviceW displayDevice,
        uint flags);

    /// <summary>Enumerates the modes a display adapter supports.</summary>
    /// <param name="deviceName">Adapter name.</param>
    /// <param name="modeIndex">Mode index, or <see cref="EnumCurrentSettings"/>.</param>
    /// <param name="devMode">Receives the mode.</param>
    /// <param name="flags">Enumeration flags.</param>
    /// <returns><see langword="true"/> while a mode exists at the index.</returns>
    [DllImport(User32, EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsExW(
        string deviceName,
        int modeIndex,
        ref DevModeW devMode,
        uint flags);

    /// <summary>AC line and battery status returned by <see cref="GetSystemPowerStatus"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    internal const byte AcLineStatusOffline = 0;
    internal const byte BatteryFlagNoSystemBattery = 128;

    /// <summary>Reads the AC line and battery status.</summary>
    /// <param name="status">Receives the status.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport(Kernel32, EntryPoint = "GetSystemPowerStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Enumerates power schemes rather than subgroups or settings.</summary>
    internal const uint AccessScheme = 16;

    /// <summary>Retrieves the CPU sets the system exposes.</summary>
    /// <param name="process">Process handle, or <see cref="IntPtr.Zero"/> for the system.</param>
    /// <param name="buffer">Destination buffer, or <see cref="IntPtr.Zero"/> to query the size.</param>
    /// <param name="bufferLength">Buffer size in bytes.</param>
    /// <param name="returnedLength">Receives the number of bytes written or required.</param>
    /// <param name="flags">Reserved; must be zero.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport(Kernel32, EntryPoint = "GetSystemCpuSetInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemCpuSetInformation(
        IntPtr buffer,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    /// <summary>Sets the CPU sets a process's threads are preferentially scheduled on.</summary>
    /// <param name="process">Process handle.</param>
    /// <param name="cpuSetIds">CPU set identifiers, or <see cref="IntPtr.Zero"/> to clear.</param>
    /// <param name="cpuSetIdCount">Number of identifiers.</param>
    /// <returns><see langword="true"/> on success.</returns>
    /// <remarks>
    /// A CPU set is a scheduling preference the kernel may override under load, which is why it is
    /// the right tool for a game and a hard affinity mask is not.
    /// </remarks>
    [LibraryImport(Kernel32, EntryPoint = "SetProcessDefaultCpuSets", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDefaultCpuSets(IntPtr process, IntPtr cpuSetIds, uint cpuSetIdCount);

    // SYSTEM_CPU_SET_INFORMATION, from the documented layout. Fixed size per entry.
    internal const int CpuSetEntrySize = 32;
    internal const int CpuSetTypeOffset = 4;
    internal const int CpuSetIdOffset = 8;
    internal const int CpuSetGroupOffset = 12;
    internal const int CpuSetLogicalProcessorIndexOffset = 14;
    internal const uint CpuSetInformationType = 0;
}
