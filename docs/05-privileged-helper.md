# 5. Privileged helper architecture

## Why there are two processes

Most of what this product does — reading topology, enumerating adapters, analysing the machine,
reading per-user settings — needs no elevation at all. A handful of operations do. Running the
whole application elevated to serve that handful would mean a large WinUI surface, a browser-grade
XAML parser, third-party packages and an update channel all executing as administrator.

So the UI stays unelevated and a small service does the privileged work:

```
┌──────────────────────────────┐        ┌──────────────────────────────┐
│  Velocity desktop / CLI      │        │  Velocity.Helper (SYSTEM)    │
│  interactive user, not       │        │  Windows service, no UI,     │
│  elevated                    │        │  no network, no plug-ins     │
│                              │        │                              │
│  engine, database, UI        │        │  4 operations                │
│                              │        │  PrivilegedOperationPolicy   │
└──────────┬───────────────────┘        └──────────────┬───────────────┘
           │                                           │
           │   \\.\pipe\velocity-helper                │
           └───────────────────────────────────────────┘
               length-prefixed JSON, request/response
```

## Attack surface, deliberately small

The helper exposes exactly eight operations:

| Operation | Purpose |
| --- | --- |
| `ping` | Liveness |
| `get-identity` | Helper version, account, protocol version |
| `registry.read` | Read one value (refused for the credential hives) |
| `registry.write` | Write or delete one value (allow-list enforced) |
| `power.get-active-scheme` | Read the active power scheme GUID |
| `power.set-active-scheme` | Activate a power scheme |
| `power.read-ac-value` | Read one AC power setting value |
| `power.write-ac-value` | Write one AC power setting value (allow-list enforced) |

There is deliberately **no DC operation**. Changing what a laptop does on battery is a battery-life
decision, and the protocol gives no way to make it — not merely no caller that does.

Power settings are allow-listed **by setting GUID, not by subgroup**. A subgroup rule would hand the
helper the whole processor power policy, including settings with real thermal implications; the
four entries in the list each correspond to a shipping module.

It has no user interface, no listening socket, no scripting host and no way to load code supplied by
the caller. It does **not** reference `Velocity.Core`: the privileged process cannot be asked to run
the engine, evaluate a tweak, or execute anything a tweak supplies.

Adding an operation is a deliberate act: a handler, an allow-list entry with a documented purpose,
and tests.

## Defence in depth

Four independent controls, in the order a request meets them:

1. **Pipe ACL.** `NamedPipeServerStreamAcl.Create` with an explicit `PipeSecurity`: full control for
   SYSTEM and Administrators; authenticated users get `ReadWrite | Synchronize` and nothing more.
   They cannot change the ACL, take ownership, or create another instance of the pipe name — so a
   rogue process cannot stand up an impostor helper. Anonymous and sandboxed tokens cannot connect.

2. **Caller verification.** On accept, the helper resolves the client process with
   `GetNamedPipeClientProcessId` and compares its image path against
   `NamedPipeChannelOptions.ExpectedClientImagePath`. When
   `ExpectedClientCertificateSubject` is configured, the image's Authenticode subject is read and
   compared too. Both are optional so a development build can run unsigned from a build directory;
   the installed configuration sets both. When neither is set, the helper logs that it is accepting
   on ACL and policy alone.

3. **Protocol validation.** `IpcServer` refuses a request before any handler sees it when the
   operation is not registered, the per-connection sequence number is not strictly increasing (a
   replayed frame), or the timestamp is outside the accepted window. A malformed frame drops the
   connection rather than attempting to resynchronise on a stream an attacker may be shaping.
   `MessageFramer` validates the length prefix against a 1 MiB limit *before* allocating.

4. **Operation policy.** `PrivilegedOperationPolicy` decides what may be written. It is allow-list
   first: a path is refused unless it matches a prefix a real module needs, with a documented
   purpose. A deny list sits on top for paths and values that fall inside an allowed prefix but must
   never be written. See [10-security-model.md](10-security-model.md).

The policy is evaluated **on the helper side**, where the caller cannot influence it. It is also
evaluated optimistically on the caller's side, purely so the user gets the same error message
whether or not the helper is in the path.

## Impersonation

The client opens the pipe with `TokenImpersonationLevel.Anonymous`. The helper does not need the
caller's identity to do its work, and refusing to hand it over removes a class of confused-deputy
problem outright.

## Degradation

`IPrivilegeContext.Channel` reports `HelperService`, `DirectElevation` or `None`. When the helper is
not running, privileged tweaks are still listed with their descriptions and current state but report
themselves as `ElevationUnavailable`, and the shell shows a single banner explaining why. The
application never silently does nothing and reports success — `TransactionalStateAccessor` throws
`PrivilegeRequiredException` rather than skipping a write.

Availability is a callback, not a snapshot, because the service can be started or stopped while the
application is open.

## Transport independence

`IpcServer`, `IpcClient` and `MessageFramer` operate on a `Stream`. Production supplies the ACL'd
named pipe; the tests supply an in-memory duplex pair. The validation logic under test is therefore
the same code that runs in production — 41 tests cover framing, dispatch, replay rejection and the
policy.

## Installation and lifecycle

The service is installed by the MSI as `VelocityHelper`, start type `Manual`, triggered by the
desktop application. It writes its own log under `%ProgramData%\Velocity\logs` with the
`velocity-helper` prefix, and runs the same redaction pipeline as the UI.
