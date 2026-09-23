# 6. Snapshot, transaction and rollback architecture

## The guarantee

> Nothing this product changes is unrecoverable, including when the process is killed mid-change.

That is not achieved by being careful. It is achieved by an ordering rule enforced in one place.

## The ordering rule

For every step of a transaction:

```
1. read the current value of every declared key
2. write the snapshot to SQLite and COMMIT          ← before any system state changes
3. only now may Apply write
4. re-read the machine to verify
5. journal the step's outcome
```

Because the snapshot reaches disk before the first write, there is no window in which a change
exists on the machine but not in the journal. If the process dies at any point after step 2, the
next launch has everything it needs to undo the change.

## Objects

| Object | Meaning |
| --- | --- |
| `OptimizationTransaction` | A unit of work applied and rolled back as a whole |
| `TransactionStep` | One tweak's participation, with its outcome and rollback payload |
| `StateSnapshot` | Pre-change values for one step |
| `SnapshotEntry` | One `StateKey` and the `StateValue` it held |

`StateKey` is a canonical address: `provider://path#item`, for example
`registry://HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl#Win32PrioritySeparation`. It
round-trips through `Parse`/`ToString`, which is how the journal stores it.

`StateValue` carries a kind alongside the data, including `Absent`. **Restoring `Absent` deletes the
item rather than writing a guessed default** — that is what keeps "the user never had this setting"
reversible.

## Lifecycle

```
                    ┌─────────┐
                    │ Pending │  journalled, nothing written yet
                    └────┬────┘
                         ▼
                    ┌──────────┐
                    │ Applying │──── crash ────┐
                    └────┬─────┘               │
             success     │      failure        │
        ┌────────────────┴───────────┐         │
        ▼                            ▼         ▼
   ┌─────────┐                ┌─────────────┐  │  next launch:
   │ Applied │                │ RollingBack │◄─┘  CrashRecoveryService
   └────┬────┘                └──────┬──────┘
        │ user rolls back            │
        └───────────────┬────────────┘
                        ▼
              ┌───────────────────┐   ┌────────────────┐
              │ RolledBack /      │   │ RollbackFailed │
              │ FailedAndRolledBack│  │ (kept, shown)  │
              └───────────────────┘   └────────────────┘
```

`Pending`, `Applying` and `RollingBack` are the states `GetIncompleteTransactionsAsync` returns.
They are exactly the states that need recovery.

## Rollback engine

Generic by design. It loads the transaction, walks the steps **in reverse ordinal order** (a later
step may depend on state an earlier one captured), and for each:

1. runs `ICustomRollback.RollbackAsync` with the persisted payload, if the tweak implements it;
2. writes every captured value back through the state providers;
3. marks the step rolled back and removes the applied-tweak record.

It is **idempotent**. Re-running it over an already restored step writes the same values again,
which matters because crash recovery cannot know how far a previous attempt got.

A failure to restore one key is recorded per key and does not abort the remaining restores. The
transaction ends in `RollbackFailed`, the journal is kept, and the Restore Center surfaces it.

## Crash recovery

`CrashRecoveryService` runs once at startup, **before the UI is shown and before any new
transaction can be opened** (`VelocityHost.StartAsync` enforces the order: migrate, then recover).
Opening a second transaction while a previous one is still journalled in flight would make the
first unrecoverable.

Recovering a `Pending` transaction — where possibly nothing was written — is intentional. Writing a
captured value back over an identical current value is a no-op, and the alternative (deciding how
far the apply got) is not knowable.

## What the tests prove

From `RollbackAndRecoveryTests`:

- the captured value is restored;
- a value that did not exist before is **deleted**, not defaulted;
- rollback is idempotent;
- steps are undone in reverse order (asserted through a rollback-order hook);
- a custom rollback receives its persisted payload;
- a crash between the write and the journal update is repaired at startup, restoring the original
  value and marking the transaction `RolledBack`;
- a crash before any write leaves the machine untouched.

## Restore Center

The data model already supports everything the Restore Center needs:

| Action | Backed by |
| --- | --- |
| Restore last optimization | `RollbackLastAsync` |
| Restore an individual tweak | `RollbackTweakAsync` |
| Restore a whole transaction | `RollbackTransactionAsync` |
| Distinguish our changes from pre-existing settings | `applied_tweak` table |
| Show what changed and when | `transaction_step` + `audit_log` |
| Export / import a snapshot | `state_snapshot` rows serialised to JSON (`IVelocityPaths.SnapshotDirectory`) |
