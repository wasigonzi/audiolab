# 1. Solution architecture

## The product in one sentence

Velocity determines which optimizations measurably improve performance on *this* gaming PC, applies
them in a way that can always be undone, and proves the result with before/after measurements.

The competitive claim is not "we have 500 registry tweaks". It is "we find the ones that work on
your machine". Everything in this architecture serves that claim, and anything that would let a
placebo change reach a user is treated as a defect.

## Product loop

```
Detect → Analyze → Snapshot → Optimize → Verify → Benchmark → Compare → Keep or roll back
```

Each arrow is a component boundary, not a phase of a script:

| Step | Owner | Notes |
| --- | --- | --- |
| Detect | `Velocity.Platform.Windows` probes | Read only, no elevation required |
| Analyze | `Velocity.Core.Hardware` | Pure functions over the detected model |
| Snapshot | `Velocity.Core.Transactions` + `Velocity.Data` | Committed to disk before any write |
| Optimize | `ITweak` implementations | Write only through the state accessor |
| Verify | `ITweak.VerifyAsync` | Re-reads the machine; never trusts the write |
| Benchmark | `Velocity.Core.Statistics` (+ Phase 9 sampling) | Frame time distributions, not averages |
| Compare | `TrialEvaluator` | Regression aware; a tail regression vetoes a mean win |
| Keep or roll back | `RollbackEngine` | Generic, driven by the snapshot, not by tweak code |

## Layering

```
                     ┌──────────────────────────────────────────┐
                     │  Velocity.App (WinUI 3)                  │
                     │  XAML views only, no logic               │
                     └───────────────┬──────────────────────────┘
                                     │
   ┌────────────────────────┐  ┌─────┴────────────────────┐
   │  Velocity.Cli          │  │  Velocity.Presentation   │
   │  headless host         │  │  view models, MVVM, nav  │
   └───────────┬────────────┘  └─────────────┬────────────┘
               │                             │
               └──────────────┬──────────────┘
                              │
                 ┌────────────┴─────────────┐
                 │      Velocity.Core       │   engine: catalogue, transactions,
                 │                          │   rollback, recovery, topology
                 └───┬───────────────┬──────┘   analysis, statistics
                     │               │
      ┌──────────────┴───┐     ┌─────┴──────────┐
      │  Velocity.Data   │     │ Velocity.      │
      │  SQLite journal  │     │ Platform.      │────► Windows APIs
      └──────────────────┘     │ Windows        │
                               └─────┬──────────┘
                                     │ named pipe
                               ┌─────┴──────────┐
                               │ Velocity.Helper│ (SYSTEM service)
                               └────────────────┘

      Velocity.Abstractions — contracts, referenced by everything, references nothing
      Velocity.Diagnostics  — logging, redaction, support bundles
      Velocity.Ipc          — protocol, framing, dispatch, authorization policy
```

### The rule that shapes everything

**Only two projects target Windows-specific APIs.** `Velocity.Platform.Windows` and
`Velocity.Helper` use a `net10.0-windows10.0.26100.0` target framework; everything else is plain
`net10.0`.

That is not stylistic. It means the entire decision-making layer — which cores a game should
prefer, whether a change is compatible, whether a snapshot can be rolled back, whether a benchmark
difference is real — is testable on any CI agent, including a Linux container. 447 tests exercise
the real engine today without a Windows machine in the loop. Windows code is confined to the parts
that genuinely need it: reading the machine and writing to it.

### Dependency rules

1. `Velocity.Abstractions` references nothing but the BCL and `Microsoft.Extensions.Logging.Abstractions`.
2. Nothing references a UI framework except the WinUI head.
3. `Velocity.Core` never references `Velocity.Platform.Windows`. Platform services arrive through
   DI as interface implementations.
4. Tweaks never reference the UI, the database, or a state provider directly. They receive a
   `TweakContext` and use `context.State`.
5. `Velocity.Helper` references `Velocity.Ipc` and `Velocity.Platform.Windows` but **not**
   `Velocity.Core`: the privileged process does not host the engine, and cannot be asked to.

## Process model

Two processes, deliberately:

- **Desktop application** — runs as the interactive user, unelevated. Owns the engine, the
  database, the UI, all decision-making.
- **Privileged helper** — a Windows service running as SYSTEM. Exposes a short, fixed operation list, applies an
  allow-list policy to each one, and has no UI, no network access and no plug-in surface.

The desktop process is never elevated in a supported installation. See
[05-privileged-helper.md](05-privileged-helper.md) and [10-security-model.md](10-security-model.md).

## Extension model

A new optimization module is a class implementing `ITweak`, registered in the container. It gains,
without writing any of it:

- catalogue listing and per-machine compatibility filtering,
- state capture before apply,
- crash-safe rollback,
- audit records,
- benchmark comparison scoping.

It cannot gain: the ability to write state it did not declare, the ability to write outside the
privileged allow list, or the ability to bypass verification.

## Commercial architecture

The product is intended to be sold. These seams exist from the first commit so that later work does not
require a rewrite:

| Requirement | Seam that supports it |
| --- | --- |
| Free/Pro tiers, licensing | `TweakDescriptor` is data; gating is a filter over the catalogue, not a change to the engine |
| Updater, signed builds | Helper verifies the caller's image path and Authenticode subject (`NamedPipeChannelOptions`) |
| Remote tweak definitions | `TweakDescriptor.DefinitionVersion` plus id-keyed journal and benchmark rows |
| Hardware/game database | `HardwareFingerprint` is non-identifying by construction and already the key for stored results |
| Localization | No user-facing string is composed in the engine's control flow; descriptors carry display text |
| Portable configuration | Every path comes from `IVelocityPaths`; nothing reads an environment variable directly |
| Consent-gated telemetry | No network client exists at all. Nothing can be sent because nothing can send |

Licensing is deliberately *not* implemented yet and deliberately kept out of the engine: an
optimization decision must never depend on a licence check.
