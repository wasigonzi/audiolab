# 11. Phase 1 status, and what is deliberately not here

This document exists so that nothing in this repository is mistaken for more than it is.

## What is implemented and verified

| Area | State | Evidence |
| --- | --- | --- |
| Solution, 14 projects, central package management | Complete | `dotnet build Velocity.sln` — 0 warnings, 0 errors |
| Contracts (`Velocity.Abstractions`) | Complete | Referenced by every layer |
| Logging, redaction, support bundles | Complete | Redacting formatter, runtime verbosity switch, zip export |
| SQLite schema, migrator, repositories | Complete | 18 tests |
| Tweak engine: catalogue, compatibility, pipeline | Complete | 87 tests |
| Transactions, snapshots, rollback, crash recovery | Complete | Covered including simulated mid-apply crash |
| CPU topology analysis | Complete | Tested against 7 machine fixtures |
| Hardware fingerprinting | Complete | Stability and sensitivity both tested |
| Benchmark statistics and keep/revert logic | Complete | Percentiles, Mann-Whitney U, bootstrap intervals, tail-regression veto |
| IPC protocol, framing, dispatch, authorization | Complete | 41 tests |
| Privileged-operation policy | Complete | Allow-list, deny-list, IFEO and mitigation rules all tested |
| Windows hardware probes | Written, compiled, **not executed** | See below |
| Privileged helper service | Written, compiled, **not executed** | See below |
| MVVM infrastructure and view models | Complete | 18 tests |
| Command line host | Complete (Windows-only at runtime) | |

**164 tests, all passing.**

## The one thing to be clear about

This phase was developed and verified on a Linux build agent with the .NET 10 SDK.

- Every portable project (`net10.0`) is **built and executed**: all 164 tests run there.
- The two Windows projects (`net10.0-windows10.0.26100.0`) are **built** — `EnableWindowsTargeting`
  makes that possible — but **cannot be executed** on this agent.

So: the Windows probes, the registry state provider, the named pipe host and the helper service
compile cleanly and their logic is reviewed, but they have not been run against a real Windows
machine. That verification is the first task of the next session, using `velocity info` and
`velocity policy` on a physical machine.

This is exactly why the architecture pushes decision-making out of the Windows layer. The parts that
decide *what to do* — topology interpretation, compatibility, transactions, rollback, statistics —
are portable and tested. The Windows layer reads and writes, and is thin by design.

## Deliberately not implemented in Phase 1

Phase 1 was scoped as infrastructure. These are absent on purpose, not overlooked:

| Not here | Arrives in | Why not now |
| --- | --- | --- |
| **Any production optimization module** | Phase 3 | The engine is complete and tested with real `ITweak` implementations in the test suite. The first shipping module is Phase 3's entire deliverable, with the full Detect → Compatibility → Snapshot → Apply → Verify → Benchmark → Rollback pipeline proven on hardware. `velocity catalogue` says so plainly rather than listing placeholders |
| **WinUI 3 dashboard** | Phase 2 | The view models it binds to exist and are tested |
| **Frame time / ETW / PDH sampling** | Phase 9 | Can only be validated on real hardware. The data model and the statistics that consume it are done; there is no fake sampler producing plausible numbers |
| **Auto-tune engine** | Phase 10 | Its decision logic (`TrialEvaluator`) is implemented and tested; it needs the Phase 9 sampler to have data |
| **Game detection, profiles UI, Optimize & Launch** | Phase 8 | Profile persistence exists; the session orchestration does not |
| **Service control, process suspension, network tweaks** | Phases 5–7 | Each needs its own state provider and policy entries |
| **Licensing, updater, crash reporting, localization** | Post-Phase-10 | Architectural seams are in place; none is implemented |
| **Windows-hosted integration tests** | Phase 3 | Needs a Windows CI leg |

## Known limitations in what *is* here

- **HAGS support detection.** `HwSchMode` tells us enabled/disabled but not whether the adapter
  supports it. Absence maps to `Unknown`, never to "off".
- **Variable refresh rate state.** No documented user-mode API exposes whether VRR is currently
  active, so it is reported as `null` rather than guessed from the driver name.
- **Integrated GPU detection** relies on the vendor plus PCI path, which is a heuristic. It affects
  display only, not any decision.
- **Storage volume mapping** uses the WMI association classes; on exotic RAID configurations the
  disk-to-volume mapping may be incomplete. The device list is still correct.
- **Vendor control panel settings** (NVIDIA low-latency mode, AMD Anti-Lag) are not readable through
  any documented Windows interface. The product does not claim to read or change them.
- **Core parking** is hidden by the platform on most modern desktops. It reports `null` rather than
  a fabricated default, and a module needing it will declare itself incompatible.

## How to verify this yourself

```bash
dotnet build Velocity.sln          # 0 warnings, 0 errors
dotnet test  Velocity.sln          # 164 tests
```

On Windows, additionally:

```powershell
dotnet run --project src\Velocity.Cli -- info      # describe this machine
dotnet run --project src\Velocity.Cli -- policy    # print the privileged allow/deny lists
dotnet run --project src\Velocity.Cli -- catalogue # no modules yet, and it says so
```
