# Velocity Gaming Optimizer

A Windows gaming optimizer whose competitive claim is **not** "500 registry tweaks". It is:

> For this PC, this Windows build, this hardware, this driver configuration and this game, these
> are the optimizations that actually improve measured performance.

Every optimization must be real, technically justified, measurable and reversible. Anything that
cannot pass that bar does not ship.

## Status: all ten phases implemented, not yet validated on hardware

The engine, persistence, transaction and rollback system, hardware detection, privileged helper,
ten optimization modules, the system advisor, game detection and per-game profiles, Optimize &
Launch, frame time capture, the Benchmark Lab and the auto-tune engine are all implemented and
tested.

**447 tests, 0 warnings.** Every one of them runs on Linux, which is both the point of the
architecture and its limit: no line of the Windows-specific code — the P/Invoke probes, the
registry provider, the named pipe transport, the ETW frame capture — has ever executed on Windows.
[docs/11-status.md](docs/11-status.md) states precisely what that means for each component and what
has to be verified before this is shipped to anyone.

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
dotnet run --project src\Velocity.Cli -- info       # what this machine is, including what no tweak can fix
dotnet run --project src\Velocity.Cli -- catalogue  # the modules, and whether each applies to this machine
dotnet run --project src\Velocity.Cli -- detect     # what each module observes, changing nothing
dotnet run --project src\Velocity.Cli -- games      # installed games, and any running now
dotnet run --project src\Velocity.Cli -- profiles   # the profiles and what each one applies
dotnet run --project src\Velocity.Cli -- benchmark  # whether frame time capture can run here
dotnet run --project src\Velocity.Cli -- autotune   # what would be trialled, and what has been measured
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
| `Velocity.Tweaks` | net10.0 | The optimization modules, portable because they reach the machine only through `IStateAccessor` |
| `Velocity.Helper` | net10.0-windows | SYSTEM service, one fixed operation list |
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
- **Detection states its evidence.** "This executable is in your library" and "something is using
  the GPU" are different claims, and the second is not made at all.
- **A restart-gated change is reported as pending, never as applied.** Auto-tune excludes such
  settings rather than measuring an unchanged machine.
- **Results never cross machines.** Benchmark and trial history are keyed by hardware fingerprint
  and workload, and no query in the code reaches past that scope.
- **There is no memory cleaner.** Emptying working sets improves the number and slows the machine
  down, so memory is reported and not "optimized".

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
| [11. Status](docs/11-status.md) | What is done, what is untested, and what must be verified on hardware |
| [12. Optimization modules](docs/12-optimization-modules.md) | Every module, what it writes, and what it claims |
| [13. Games and sessions](docs/13-games-and-sessions.md) | Detection, profiles and Optimize & Launch |
| [14. Frame capture and auto-tune](docs/14-frame-capture-and-autotune.md) | How a measured verdict is reached |

## What was built

| Phase | Deliverable | State |
| --- | --- | --- |
| 1 | Foundation: engine, journal, rollback, probes, helper, statistics | Implemented |
| 2 | WinUI 3 dashboard and live telemetry | Implemented |
| 3 | First complete optimization module end to end | Implemented |
| 4 | CPU engine: process control, CPU sets, priority, core placement | Implemented |
| 5 | Windows background reduction and the service analyzer | Implemented |
| 6 | Network: measurement first, and only settings the driver publishes | Implemented |
| 7 | GPU, memory, storage and power, plus the system advisor | Implemented |
| 8 | Game detection, per-game profiles, Optimize & Launch | Implemented |
| 9 | ETW frame time capture and the Benchmark Lab | Implemented |
| 10 | Auto-tune | Implemented |

## What is left before this ships

Implemented is not shipped. The honest list is in
[docs/11-status.md](docs/11-status.md); the short version:

1. **Run it on Windows.** Nothing Windows-specific has ever executed. The probes, the registry
   provider, the named pipe transport and the ETW capture are written against documented APIs and
   reviewed, not run.
2. **Validate each module on real hardware,** with the Benchmark Lab, on more than one machine.
   Until then every `ExpectedEffect` in the catalogue is a literature claim, not a measurement.
3. **Sign the helper and write an installer** that registers the service with a correct ACL.
4. **Test the rollback path against a hard power loss,** not only against a simulated one.
