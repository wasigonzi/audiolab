# Velocity Gaming Optimizer

A Windows gaming optimizer whose competitive claim is **not** "500 registry tweaks". It is:

> For this PC, this Windows build, this hardware, this driver configuration and this game, these
> are the optimizations that actually improve measured performance.

Every optimization must be real, technically justified, measurable and reversible. Anything that
cannot pass that bar does not ship.

## Status: Phase 1 — foundation

The engine, persistence, transaction/rollback system, hardware detection, privileged-helper
architecture, MVVM infrastructure and benchmark statistics are implemented and tested. The WinUI
dashboard (Phase 2) and the first production optimization module (Phase 3) are not here yet, and
[docs/11-phase1-status.md](docs/11-phase1-status.md) says exactly what is and is not implemented.

**164 tests, 0 warnings.**

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build Velocity.sln
dotnet test  Velocity.sln
```

The portable projects build and test on any platform, including Linux CI agents — that is a
deliberate architectural property, not an accident. The two Windows projects build anywhere
(`EnableWindowsTargeting`) but run only on Windows.

## Try the engine

On Windows:

```powershell
dotnet run --project src\Velocity.Cli -- info       # what this machine is, and what the analyzer makes of it
dotnet run --project src\Velocity.Cli -- policy     # exactly what the privileged helper may and may not write
dotnet run --project src\Velocity.Cli -- history    # what this product has changed on this machine
dotnet run --project src\Velocity.Cli -- rollback   # put it back
```

## How it is put together

| Project | Target | Role |
| --- | --- | --- |
| `Velocity.Abstractions` | net10.0 | Contracts and immutable models |
| `Velocity.Diagnostics` | net10.0 | Logging, redaction, support bundles |
| `Velocity.Data` | net10.0 | SQLite journal, snapshots, audit, benchmarks |
| `Velocity.Core` | net10.0 | Engine: catalogue, transactions, rollback, topology analysis, statistics |
| `Velocity.Ipc` | net10.0 | Helper protocol, framing, authorization policy |
| `Velocity.Platform.Windows` | net10.0-windows | Hardware probes, registry provider, pipe transport |
| `Velocity.Helper` | net10.0-windows | SYSTEM service, four operations |
| `Velocity.Presentation` | net10.0 | MVVM view models, no XAML |
| `Velocity.Cli` | net10.0-windows | Headless host and composition root |

Only two projects touch Windows APIs. Everything that *decides* anything is portable and tested.

## Principles the code enforces

- **A tweak cannot write state it did not declare.** Undeclared writes were never snapshotted, so
  they cannot be rolled back; the transaction fails.
- **The snapshot is committed to disk before the first write.** There is no window in which a change
  exists on the machine but not in the journal.
- **A write is not a success until the machine is re-read.** Verification failure is treated as
  apply failure.
- **Security is never traded for frames.** Defender, the firewall, Secure Boot, BitLocker, LSA,
  Windows Update and the speculative-execution mitigations are refused by policy. Their performance
  impact is reported; changing them stays the user's decision, made in Windows.
- **A neutral measurement means revert.** An unnecessary change to someone's operating system is a
  cost even when it is free in frames.
- **"Not exposed by this driver" is not "off".** Capability values are three-valued throughout.

## Documentation

| | |
| --- | --- |
| [1. Solution architecture](docs/01-solution-architecture.md) | Layering, process model, extension and commercial seams |
| [2. Project structure](docs/02-project-structure.md) | Every folder and what lives in it |
| [3. Technology choices](docs/03-technology-choices.md) | What was chosen, what was rejected, and why |
| [4. Tweak engine](docs/04-tweak-engine.md) | `ITweak`, descriptors, compatibility, the pipeline |
| [5. Privileged helper](docs/05-privileged-helper.md) | Two-process model and its four controls |
| [6. Transactions and rollback](docs/06-transactions-and-rollback.md) | The ordering rule and crash recovery |
| [7. Hardware detection](docs/07-hardware-detection.md) | Probes, topology analysis, fingerprinting |
| [8. Database schema](docs/08-database-schema.md) | Tables, conventions, migrations |
| [9. Benchmark and telemetry](docs/09-benchmark-and-telemetry.md) | Frame-time statistics and the keep/revert rule |
| [10. Security model](docs/10-security-model.md) | Allow-list policy, threat model, personal data |
| [11. Phase 1 status](docs/11-phase1-status.md) | What is done, what is not, and what is untested |

## Roadmap

| Phase | Deliverable |
| --- | --- |
| **1** | **Foundation — done** |
| 2 | WinUI 3 glassmorphism dashboard and live telemetry |
| 3 | First complete optimization module, proven end to end on hardware |
| 4–7 | CPU, Windows background, network, then GPU/memory/storage/power modules |
| 8 | Game profiles and Optimize & Launch |
| 9 | ETW/PDH telemetry and the Benchmark Lab |
| 10 | Auto-tune |
