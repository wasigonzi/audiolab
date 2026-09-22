# 3. Technology choices and justification

## .NET 10 / C# 13

Current LTS. Chosen over .NET Framework for `LibraryImport` source-generated P/Invoke (no runtime
marshalling stubs), `TimeProvider` (testable clocks), file-scoped namespaces, collection
expressions and the modern analyzer set.

`Directory.Build.props` enables `TreatWarningsAsErrors`, nullable reference types and
`GenerateDocumentationFile` for every project. A tool that edits operating system state should not
compile with warnings, and requiring an XML summary on every public member keeps intent attached to
the code rather than to a wiki.

## WinUI 3 / Windows App SDK (Phase 2)

Required by the visual brief: Mica and Acrylic backdrops, rounded geometry, 60+ fps compositor-
driven animation. WPF cannot produce Mica without interop hacks; WinForms cannot produce it at all;
Electron would make the optimizer itself one of the heaviest background processes on the machine,
which would be self-defeating.

The mitigation for WinUI's weaknesses (harder to test, Windows-only tooling) is that **no logic
lives in the UI project**. `Velocity.Presentation` holds the view models and has no WinUI
reference, so it is unit tested on any platform. The XAML head is intended to be a thin layer of
bindings.

## MVVM with CommunityToolkit.Mvvm

Source-generated `ObservableProperty` and `RelayCommand`, so no hand-written
`INotifyPropertyChanged` plumbing and no runtime reflection. `ViewModelBase` adds the three things
every page needs: busy state, error capture, and a cancellation token tied to the page's lifetime —
the last one matters because the optimizer must go quiet while a game is running.

## Microsoft.Extensions.DependencyInjection / Hosting

Standard, well understood, and already the composition model the Windows service host expects.
Constructor injection everywhere; no service locator in the engine. `TweakContext` is the single
object a tweak is given, which is what makes tweaks testable against a fabricated machine.

## SQLite via Microsoft.Data.Sqlite

The journal has to survive a process kill and be readable by a second process (the helper). That
rules out an in-process file format or JSON-on-disk. SQLite gives durable, atomic commits with WAL
and a busy timeout, and a support engineer can open the file with any SQLite tool.

Microsoft.Data.Sqlite rather than System.Data.SQLite: smaller, actively maintained, and no native
mixed-mode assembly.

Raw ADO.NET rather than an ORM. The schema is small and mostly append-only, the queries are known
in advance, and an ORM's change tracker is an unhelpful abstraction for a journal whose entire
purpose is "these exact rows must be on disk before the next line of code runs".

## Serilog behind Microsoft.Extensions.Logging

The abstraction stays `ILogger<T>`, so no component depends on Serilog. Serilog supplies the
rolling file sink and, importantly, a pluggable `ITextFormatter`: redaction is applied to the
*rendered* output, which catches personal data embedded in exception messages and stack traces that
property-level scrubbing misses.

A `LoggingLevelSwitch` lets the gaming session manager raise the minimum level for the duration of a
session, so the optimizer is not writing to disk while it is supposed to be protecting frame times.

## System.Text.Json, source generated for the IPC protocol

The helper runs as SYSTEM and parses messages from a less trusted process. Source-generated
serialization means no reflection-driven type discovery on that path, and the contract is a closed
set of types.

## Native Windows APIs over PowerShell and command line tools

PowerShell is not the optimization engine and never shells out on a hot path. Where the product
reads or writes system state it uses documented Windows APIs:

| Need | API |
| --- | --- |
| Processor topology, caches, NUMA, groups, dies | `GetLogicalProcessorInformationEx` |
| Memory state | `GlobalMemoryStatusEx`, `GetPerformanceInfo` |
| Power schemes and processor policy | `PowerGetActiveScheme`, `PowerEnumerate`, `PowerReadACValueIndex` |
| Displays and modes | `EnumDisplayDevices`, `EnumDisplaySettingsEx` |
| AC/battery state | `GetSystemPowerStatus` |
| Adapters, drivers, chassis, disks | WMI (`System.Management`) |
| Adapter capabilities | Network class registry keywords |
| IPC | Named pipes with an explicit ACL |

There are **no undocumented syscalls and no `NtQuerySystemInformation` structure guessing**. An
optimizer that breaks on the next Windows update is worse than one that reports a subsystem as
unavailable.

## ETW and performance counters (Phase 9)

Frame time capture, DPC/ISR observation and background CPU attribution require ETW
(`Microsoft.Diagnostics.Tracing.TraceEvent`) and PDH. They are deliberately **not** stubbed in
Phase 1: the telemetry data model exists, the statistics that consume it are implemented and tested,
and the sampling arrives with the phase that can validate it on real hardware.

## What was rejected

| Option | Why not |
| --- | --- |
| Electron / web stack | The optimizer would become a top background CPU and memory consumer |
| An ORM | Wrong abstraction for a crash-recovery journal |
| PowerShell as the engine | Slow to start, hard to error-handle, impossible to unit test, and encourages copy-pasted tweak scripts |
| A single monolithic elevated process | Requires the entire UI to run as administrator |
| Vendor SDKs (NVAPI, ADL) in Phase 1 | Redistribution and stability questions; Windows-exposed settings come first, and the product must not claim to control what it cannot |
