# 12. Optimization modules

Ten modules ship. Each one states what it writes, why that could matter, and what it does not
claim. This document is the index; the authoritative text for each module is the XML documentation
on its class and the `TechnicalDescription` and `ExpectedEffect` in its descriptor, which is what
the UI shows the user.

All ten live in `Velocity.Tweaks`, which targets `net10.0` and has **no Windows reference**. They
reach the machine only through `IStateAccessor`, so every one of them runs end to end in tests
against an in-memory machine, through the real engine, journal and rollback path.

## CPU

| Module | Writes | What it does |
| --- | --- | --- |
| `cpu.scheduler-quantum` | `PriorityControl!Win32PrioritySeparation` | Sets short, variable quantums with a foreground boost |
| `cpu.background-priority` | Per-process priority | Lowers the priority of non-protected background processes |
| `cpu.game-core-placement` | Process CPU sets | Confines a game to one cache-coherent core complex |

`cpu.game-core-placement` is the module the topology analyzer exists for. On a dual chiplet Ryzen
with asymmetric cache it keeps a game on the chiplet with the larger L3; on a hybrid Intel part it
keeps it off the efficiency cores. It uses CPU sets rather than a hard affinity mask, because a set
is a preference the scheduler may override under load — a badly chosen affinity mask can deadlock a
thread pool, a badly chosen CPU set only loses a little performance.

It returns `NotVerifiable` from `VerifyAsync`: Windows exposes no way to read back a process's
default CPU sets, so the module says it cannot confirm the change rather than asserting it worked.

## Windows behaviour

| Module | Writes | What it does |
| --- | --- | --- |
| `windows.game-recording` | `GameDVR`, `GameBar`, `GameConfigStore` values | Turns off background game recording |
| `windows.session-service` | `Multimedia\SystemProfile` values | Adjusts the multimedia class scheduler's reserved share |

## Network

| Module | Writes | What it does |
| --- | --- | --- |
| `network.adapter-power-management` | `PnPCapabilities` on the adapter's driver key | Stops Windows powering the adapter down |
| `network.energy-efficient-ethernet` | `*EEE` | Disables the link's low-power idle state |
| `network.interrupt-moderation` | `*InterruptModeration` | Trades CPU for lower packet delivery latency |

The rule these three enforce: **a keyword the driver does not publish is not a setting.** If the
value is absent from the adapter's driver key, the module reports itself unsupported rather than
creating the value — writing a keyword a miniport does not implement is how a tool appears to work
and does nothing. A test asserts that none of them claims to reduce ping.

Network quality is *measured* by `NetworkQualityAnalyzer` before anything is offered.

## GPU, power and the rest

| Module | Writes | What it does |
| --- | --- | --- |
| `gpu.hardware-scheduling` | `GraphicsDrivers!HwSchMode` | Moves GPU work scheduling to the GPU |
| `power.performance-plan` | The active power scheme | Stops the processor clocking down between frames |

`gpu.hardware-scheduling` is the clearest case in the product for measuring rather than asserting.
Published results are genuinely mixed — some titles gain, some lose, some driver versions have been
unstable — so its descriptor says exactly that, it returns `PendingRestart` rather than claiming
effect, and auto-tune deliberately excludes it because a restart-gated change cannot be measured in
one sitting.

`power.performance-plan` captures the GUID of the plan the user actually had, rather than assuming
Balanced, and refuses to run on a laptop that is not plugged in. Only the AC side of any power
setting is ever written; there is no code path, and no IPC operation, that reaches the DC side.

## What the advisor reports instead of fixing

`SystemAdvisor` produces findings for things no module can change:

- commit charge near the limit, and low available memory;
- a single memory module (single channel), or modules at mismatched speeds;
- background applications holding a lot of memory;
- Windows installed on a mechanical disk;
- a volume close to full;
- **a monitor running below its maximum refresh rate**, which is the most common real defect on
  gaming PCs and which no registry change compensates for.

There is deliberately **no memory optimization module**. Emptying working sets or the standby list
makes the available-memory number rise and makes the machine slower, because the next access has to
fault the data back in. A test asserts that no advisor finding recommends anything of the kind.

## Adding a module

1. Implement `ITweak` in `Velocity.Tweaks`, addressing state only through `context.State`.
2. Declare every key in `GetStateKeysAsync`. The engine refuses an undeclared write, because a write
   that was never snapshotted cannot be rolled back.
3. Write `TechnicalDescription` so a sceptical reader can verify the mechanism, and `ExpectedEffect`
   so it survives being measured. If the honest answer is "often nothing", say so.
4. Return `PendingRestart` rather than success when the change is not yet in effect, and
   `NotVerifiable` when the platform cannot be re-read.
5. If the key is under `HKLM`, add an allow-list entry with a documented purpose in
   `PrivilegedOperationPolicy`; the helper refuses anything else.
6. Register it in `TweaksServiceCollectionExtensions`, and add a test that drives it through
   `EngineHarness`.
