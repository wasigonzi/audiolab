# 7. Hardware detection architecture

## Separation of reading from interpreting

```
Windows APIs ──► probes ──► raw model ──► analyzer ──► interpreted model ──► decisions
               (platform)  (Abstractions)  (Core)         (Core)
```

The Windows probes produce a faithful transcription of what the OS reports and interpret nothing.
All interpretation — hybrid classification, CCD inference, which cores a game should prefer —
happens in pure functions over the model.

That split is what makes the hard part testable. Nobody has an Intel hybrid part, a single-CCD
Ryzen, a dual-CCD X3D part, a laptop and a two-processor-group workstation on the desk at the same
time; `Velocity.TestSupport` reproduces the topology Windows reports for all of them, and the
analyzer is tested against all of them on every build.

## Probe contract

```csharp
public interface IHardwareProbe<TResult>
{
    string ProbeName { get; }
    Task<TResult> ProbeAsync(CancellationToken cancellationToken);
}
```

Probes are **read only by contract**. Anything that changes system state is a tweak, never a probe,
so "look at my machine" can be offered without elevation and without risk.

Specific interfaces: `ICpuTopologyProbe`, `IOperatingSystemProbe`, `IMemoryProbe`, `IGpuProbe`,
`IStorageProbe`, `INetworkProbe`, `IDisplayProbe`, `IPowerProbe`, `IPlatformSecurityProbe`,
`IMachineKindProbe`.

## Degradation, not failure

`SystemProfileProvider` treats three probes as required (processor, operating system, memory) and
the rest as optional. An optional probe that throws is recorded in
`SystemProfile.ProbeFailures` and the profile is still produced.

The failure is never hidden. It is shown on the System page, printed by `velocity info`, and modules
that depend on the missing data report themselves incompatible rather than operating on a guess.
That is the difference between a degraded product and a dangerous one.

## Processor topology

`GetLogicalProcessorInformationEx(RelationAll)` returns a sequence of variable-length records.
Several relationship payloads end in a variable-length `GROUP_AFFINITY` array, which a fixed managed
struct cannot express, so `WindowsCpuTopologyProbe` walks the buffer by documented offsets. Every
offset is a named constant with the layout it comes from.

Extracted: processor groups and their active masks, physical cores with their group-relative
affinity masks and Windows efficiency class, logical processors, NUMA nodes, every cache instance
with the processors that share it, and dies where the platform reports `RelationProcessorDie`.

Vendor, brand string and base frequency come from
`HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0`.

### Group-relative addressing

Windows addresses processors as (group, index-within-group). Affinity masks are group-relative, so
`GroupId` travels with `GroupRelativeIndex` everywhere an affinity decision is made.
`GlobalIndex` exists only to give the UI and telemetry a flat identifier. On a machine with more
than one group, the analyzer says so explicitly in its notes.

## Topology analysis

`CpuTopologyAnalyzer.Analyze` produces a `CpuLayout`:

**Hybrid classification.** Windows reports `EfficiencyClass` per core; higher is more performant.
More than one distinct class means hybrid. The highest class is the performance set. With three or
more classes the lowest is treated as a low-power cluster. On a homogeneous part every core reports
class 0 — which is why class 0 alone never implies "efficiency core".

**Core complexes.** Windows does not expose "this is CCD 1". The one documented signal it gives is
which logical processors share each cache instance, so complexes are derived from last-level cache
sharing: one L3 instance is one complex. On Zen 2 and later that boundary coincides with a CCD.
Where `RelationProcessorDie` is reported, the reported dies label the complexes instead of inferring
the label from cache size.

**Preferred game cores — a hypothesis, not a claim.**

| Machine | Candidate set | Reasoning |
| --- | --- | --- |
| Hybrid, ≥4 P-cores | performance cores | avoid the scheduler parking a render thread on an E-core |
| Hybrid, <4 P-cores | all cores | too few to confine to |
| >1 L3 complex, best has ≥4 cores | the complex with the largest L3 (ties: most cores, then lowest id) | largest-cache complex is the stacked-cache CCD on X3D parts; avoids cross-complex migration |
| >1 L3 complex, all equal cache | one complex, stated as "purely to keep threads on one complex" | |
| Single complex | all cores | there is no locality to gain |
| Fewer than 4 candidate cores | all cores | confining would starve the game |

Background cores are the remainder, and only when at least two are left — otherwise background
threads queue behind each other and the stutter moves rather than disappearing.

**What the analyzer never does:** disable SMT or efficiency cores. Neither is reliably a win, and
neither is reversible from Windows.

Whether confining a game to the candidate set actually helps is decided by the auto-tune engine
measuring it, never by this record.

## Other subsystems

| Subsystem | Source | Note |
| --- | --- | --- |
| Operating system | `Environment.OSVersion` + `CurrentVersion` registry | UBR and `DisplayVersion` matter: compatibility changes within a build number. Windows 11 still reports "Windows 10" in `ProductName`; the build number is the discriminator |
| Memory | `GlobalMemoryStatusEx`, `GetPerformanceInfo`, `Win32_PhysicalMemory` | Commit charge and limit are reported, not just "available": a machine near its commit limit stutters even with free RAM |
| GPU | `Win32_VideoController` + display class registry | `AdapterRAM` is 32-bit and wraps above 4 GB, so dedicated memory comes from `HardwareInformation.qwMemorySize`, matched by PnP prefix |
| HAGS | `GraphicsDrivers!HwSchMode` | 1 disabled, 2 enabled, absent → `Unknown`, which is not the same as "off" |
| Storage | `MSFT_PhysicalDisk` (fallback `Win32_DiskDrive`) | Only the storage namespace distinguishes NVMe from SATA SSD; knowing what is solid state is what stops a defragmentation recommendation |
| Network | `NetworkInterface` + network class keywords | Capabilities are three-valued: enabled, disabled, or *not exposed by this driver* |
| Display | `EnumDisplayDevices`, `EnumDisplaySettingsEx` | Enumerating modes is what makes "your 240 Hz monitor is at 60 Hz" detectable |
| Power | `PowerGetActiveScheme`, `PowerEnumerate`, `PowerReadACValueIndex` | AC values only; a hidden setting returns null rather than a fabricated default |
| Security features | `Win32_DeviceGuard`, `Win32_ComputerSystem` | Reported, never changed |
| Machine kind | `Win32_SystemEnclosure` chassis types, VM markers | Desktop / laptop / handheld / VM changes which modules are appropriate |

## Hardware fingerprint

`HardwareFingerprintFactory` produces SHA-256-derived hashes for CPU, GPU, memory and OS, plus a
composite. It answers one question: *would a benchmark taken earlier still be evidence about this
machine today?* A driver update changes the answer, so the driver version is in the GPU hash. A
different user name does not, so no user identity is included.

Adapters are ordered by device instance id before hashing so PCIe enumeration order does not change
the fingerprint between boots.

Nothing hashed is a serial number, MAC address, machine name or user name. That is what makes the
same key safe to use in a future shared hardware database.
