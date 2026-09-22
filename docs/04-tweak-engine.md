# 4. Tweak engine architecture

## The contract

```csharp
public interface ITweak
{
    TweakDescriptor Descriptor { get; }

    IReadOnlyList<StateKey> GetStateKeys(TweakContext context);

    Task<CompatibilityResult> CheckCompatibilityAsync(TweakContext context, CancellationToken ct);
    Task<TweakObservation>    DetectAsync(TweakContext context, CancellationToken ct);
    Task<ApplyResult>         ApplyAsync(TweakContext context, CancellationToken ct);
    Task<VerificationResult>  VerifyAsync(TweakContext context, CancellationToken ct);
}
```

There is no `Rollback()`. That is the central design decision.

## Why rollback is not a tweak's job

A hand-written undo path is written once, exercised only when something has already gone wrong, and
drifts from the apply path with every change. Instead a tweak *declares* the state it will touch,
the engine captures it before apply, and rollback is a generic loop that writes the captured values
back through the same providers.

The consequence is enforced rather than trusted: `TransactionalStateAccessor` refuses a write to a
key the tweak did not declare, because an undeclared write was never snapshotted and therefore
cannot be undone. The transaction fails and everything already applied is reversed.
(`OptimizationEngineTests.Apply_RejectsAWriteToAKeyTheTweakDidNotDeclare`.)

Effects that genuinely cannot be expressed as state — a suspended process, for instance — implement
`ICustomRollback` and store a small payload during apply. The payload is persisted with the
transaction, so the custom rollback survives a crash too.

## Descriptor: metadata as data

`TweakDescriptor` carries everything the UI, catalogue and licensing layer need without executing
anything: id, name, category, summary, technical description, expected effect, risk, scope
(session or persistent), elevation, restart, whether a benchmark is recommended, Windows build
range, declarative hardware requirements, and a definition version.

`ExpectedEffect` is written honestly, including "no measurable effect on many systems" where that
is the truth. A descriptor that promises an FPS number is a bug.

`DefinitionVersion` is incremented whenever behaviour or declared keys change; journal rows and
benchmark results record it, so history remains interpretable after a module is revised.

## Compatibility: two gates

1. **Declarative** — `CompatibilityEvaluator` checks Windows build range, elevation availability,
   CPU/GPU vendor, minimum cores, hybrid requirement, multiple core complexes, and excluded machine
   kinds. This runs without instantiating anything, so the catalogue can be filtered for the UI
   cheaply.
2. **Specific** — `CheckCompatibilityAsync` handles what only the module can determine.

A compatibility check that throws is treated as "not supported". Failing closed is the difference
between "we could not tell" and "we assumed it was fine".

An incompatible tweak is **skipped, not failed**: the machine simply does not have the feature, and
the rest of a profile should still apply.

## Execution pipeline

For each tweak in a transaction:

```
CheckCompatibility ──not supported──► journal a skipped step, continue
        │ supported
        ▼
     Detect                      (record what the machine looks like now)
        ▼
  GetStateKeys → read each → StateSnapshot → COMMIT TO DISK
        ▼
      Apply                      (writes only through context.State)
        ▼
     Verify                      (re-read the machine; never trust the write)
        ▼
  journal the step, record the tweak as applied, write an audit row
```

Verification failure is treated exactly like apply failure. A write that "succeeded" but did not
change the machine is the single most common way optimizer software lies to its users.

## Options and profiles

A profile supplies per-tweak options through `TweakContext.Options`. Options are strings with typed
accessors (`GetOption(name, default)`), so a profile file that predates a new option still loads.

## Thread safety

One tweak instance is registered in the container and may be evaluated concurrently for detection
while a transaction runs. Implementations must be stateless; all per-run state lives on
`TweakContext`.

## Testing a tweak without Windows

Because a tweak only reaches the machine through `context.State`, a test can construct a
`TweakContext` over `InMemoryStateProvider` and a fixture `SystemProfile`, and run the genuine
apply, verify and rollback paths. `ScriptedTweak` in `Velocity.TestSupport` does exactly this, and
the engine tests drive it through the real coordinator.
