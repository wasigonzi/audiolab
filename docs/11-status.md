# 11. Status

This document exists so that nobody — including a future maintainer, and including the author —
can mistake "implemented" for "shipped". It states what is built, what has been executed, and what
has to happen before this product is put in front of a paying user.

## The single most important fact

**No Windows-specific code in this repository has ever run on Windows.**

Everything was written and tested on a Linux build agent. That is a deliberate architectural
property: only two projects (`Velocity.Platform.Windows` and `Velocity.Helper`) touch Windows APIs,
and everything that *decides* anything is portable, which is why 447 tests are meaningful. But it
has a hard limit. The P/Invoke probes, the registry state provider, the named pipe transport, the
service and process controllers and the ETW frame capture are written against documented APIs and
reviewed line by line; they are not verified.

Where a Windows API's behaviour is subtle, the code says so in a comment and the surrounding design
assumes it might be wrong. That is mitigation, not verification.

## Implemented and tested on the build agent

| Area | State |
| --- | --- |
| Tweak engine: catalogue, compatibility, apply, verify | Tested through the real engine |
| Transactions, snapshots, rollback, crash recovery | Tested against a real SQLite database |
| State providers and the undeclared-write guard | Tested |
| CPU topology analysis across 7 machine fixtures | Tested |
| Process classification, protection list, core placement | Tested |
| Service classification and dependency analysis | Tested |
| Ten optimization modules | Tested end to end against an in-memory machine |
| System advisor (memory, storage, displays) | Tested |
| IPC framing, replay rejection, authorization policy | Tested |
| Game library parsing (Steam, Epic) and detection | Tested against real manifest text |
| Profiles, per-game resolution, request construction | Tested |
| Optimize & Launch, including every restore path | Tested |
| Frame interval extraction and the Benchmark Lab | Tested |
| Auto-tune loop and trial persistence | Tested |
| Statistics: percentiles, Mann-Whitney U, bootstrap CIs | Tested |
| View models | Tested, with no UI framework reference |

## Written but never executed

| Area | Risk if wrong |
| --- | --- |
| `GetLogicalProcessorInformationEx` buffer parsing | Wrong topology, so wrong core placement |
| CPU set enumeration and `SetProcessDefaultCpuSets` | Placement silently does nothing |
| Registry state provider | The central write path of the product |
| Named pipe ACL, caller verification, Authenticode check | The security boundary itself |
| Service and process controllers | Session-scoped changes fail to apply or to revert |
| WMI-backed probes (GPU, storage, memory modules) | Detection is wrong or empty |
| `powrprof` reads and writes | Power plan changes do nothing, or do not revert |
| ETW frame capture | No measurement at all, or a wrong one |
| WinUI 3 application head | Has never been compiled: the XAML compiler is Windows-only |

The WinUI head deserves emphasis: `Velocity.App` is **excluded from the solution filter** the CI
Linux job builds, because the XAML compiler cannot run on Linux. The Windows CI job builds the full
solution, so the first real compile of the UI happens there.

## Claims this product is not yet entitled to make

Every module's `ExpectedEffect` is currently sourced from vendor documentation, the published
behaviour of the API being called, and widely reproduced measurements. **None of it has been
measured by this product on real hardware.** Until the Benchmark Lab has been run against each
module on more than one machine, the catalogue describes a mechanism, not a result — and the text
in each descriptor is written to be defensible on that basis.

The modules most likely to measure as no difference on a modern machine, and which say so in their
own descriptors, are the power plan module and the scheduler quantum module.

## Before this ships

1. **Run every Windows path on Windows**, starting with the probes and the helper's pipe, and
   including a test that the helper refuses an unauthorized caller.
2. **Benchmark each module on at least three machines**, spanning an Intel hybrid part, a dual
   chiplet Ryzen and a laptop. Rewrite any `ExpectedEffect` the measurements contradict; delete any
   module that measures as nothing on every machine.
3. **Verify rollback against a hard power loss** — pulling power mid-transaction, not simulating it
   by rewriting the journal.
4. **Sign the helper binary and write the installer**, which must register the service with an ACL
   that only allows the intended caller, and must remove it cleanly.
5. **Have the IPC boundary reviewed by someone who did not write it.** It is a SYSTEM service
   listening on a named pipe; that is the part where being wrong is expensive.
6. **Decide the licensing model.** Nothing in the engine knows about licensing today, which is
   intentional and should survive whatever is chosen.

## What is deliberately absent

These are not omissions to be filled in later. They are decisions:

- **No memory cleaner.** Emptying working sets or the standby list makes "available memory" rise
  and makes the machine slower.
- **No universal service disable list.** Services are classified by dependency and role; a list
  would be wrong on some machine.
- **No security setting is weakened for frames.** Defender, the firewall, Secure Boot, BitLocker,
  LSA, Device Guard, Windows Update and the speculative-execution mitigations are refused by the
  helper's policy, whatever a module asks for.
- **No claim that a registry value reduces ping.** The network modules change adapter behaviour that
  the driver publishes, and say what that does.
- **No frame rate claim without a before measurement.** A gaming session report carries resource
  counters and explicitly not frame times.
- **No invasive DRM.** Licensing is not present in the engine at all.
