# 10. Security model

## Principles

1. **Never weaken security for frames.** Not silently, not behind a toggle labelled "advanced", not
   in an "experimental" bucket.
2. **Least privilege.** The UI runs unelevated; a four-operation service does the privileged work.
3. **Allow-list, not deny-list.** A privileged write is refused unless something explicitly permits
   it, with a documented purpose.
4. **The boundary is enforced where the caller cannot reach it** — on the helper side.
5. **No personal data leaves the machine, and as little as possible reaches disk.**

## Never modified

`PrivilegedOperationPolicy` refuses these outright, and tests assert each one:

| Protected | Why |
| --- | --- |
| `HKLM\SOFTWARE\Microsoft\Windows Defender`, `…\Policies\…\Windows Defender` | antimalware configuration |
| `HKLM\SYSTEM\CurrentControlSet\Services\WinDefend`, `SecurityHealthService`, `wscsvc` | antimalware and Security Center services |
| `…\Services\MpsSvc`, `BFE`, `SharedAccess` | firewall and filtering engine |
| `…\Control\SecureBoot`, `DeviceGuard`, `CI`, `Lsa`, `BitLocker` | Secure Boot, VBS, code integrity, LSA, disk encryption |
| `…\CurrentVersion\WindowsUpdate`, `Policies\…\WindowsUpdate` | update configuration |
| `…\CurrentVersion\Run`, `RunOnce`, `Winlogon` | persistence and logon shell |
| `Memory Management!FeatureSettings*` | speculative-execution mitigation control |
| Image File Execution Options, except `…\<exe>\PerfOptions` | the key is a process-hijack primitive; only `CpuPriorityClass`, `IoPriority`, `PagePriority`, `WorkingSetLimitInKB` are reachable |
| `HKLM\SAM`, `HKLM\SECURITY` | credential hives — not even readable |
| `HKLM\SYSTEM\CurrentControlSet\Policies` | machine policy |

The mitigation entries and the IFEO rule are the interesting ones: both sit **inside** an otherwise
allowed prefix. That is precisely why a deny list layered over the allow list exists.

## Performance-relevant security settings

VBS, HVCI and the speculative-execution mitigations do cost measurable performance on some
workloads. The product's position:

- **Report them.** `WindowsPlatformSecurityProbe` reads VBS, HVCI, Core Isolation availability and
  hypervisor presence, and the System page shows them.
- **Explain them.** Where a setting affects gaming performance it is presented separately with its
  measured impact, its security consequence, its current state, and a link to Microsoft's
  documentation.
- **Never change them.** Not through the helper, not through a script, not behind a warning dialog.
  The user changes them in Windows if they choose to.

A gaming optimizer that ships a "disable memory integrity for +3% FPS" button is selling a security
downgrade as a performance feature.

## Privileged channel controls

Summarised from [05-privileged-helper.md](05-privileged-helper.md):

1. Pipe ACL — SYSTEM and Administrators full control; authenticated users get `ReadWrite |
   Synchronize` only, so they cannot alter the ACL or create a competing pipe instance.
2. Caller verification — client process image path, and Authenticode subject when configured.
3. Protocol validation — registered operations only; strictly increasing per-connection sequence
   numbers (replay rejection); timestamp window; 1 MiB frame cap validated before allocation;
   malformed frame drops the connection.
4. Operation policy — allow-list with documented purposes, deny-list on top, evaluated helper-side.
5. Anonymous impersonation level — the SYSTEM service cannot impersonate the caller's token.

## Code integrity

- Release builds are signed; the helper can require the caller's Authenticode subject to match.
- No dynamic code loading in the helper: no scripts, no plug-ins, no caller-supplied assemblies.
- `LibraryImport` source generation only; no `DllImport` of a path the caller can influence.
- Only documented Windows APIs. No undocumented syscalls, no `NtQuerySystemInformation` structure
  guessing.

## Personal data

| Data | Treatment |
| --- | --- |
| User name, machine name, profile path | Redacted to `<user>`, `<machine>`, `<user-profile>` by `SensitiveDataRedactor` |
| MAC addresses | Redacted to `<mac>` |
| Registry paths containing a SID | Redacted by the same pipeline before reaching the audit table |
| Hardware fingerprint | Derived only from structural facts — no serials, no MACs, no names |
| Telemetry | No network client exists. Nothing can be sent because nothing can send |

Redaction is applied at the log **formatter** and inside `AuditSink`, not at call sites, so a new
module cannot leak personal data by forgetting to call it. Support bundles are re-redacted on the
way into the archive, so a bundle stays clean even if a log file predates a redaction rule.

## Threat model

| Threat | Mitigation |
| --- | --- |
| Malware drives the helper to gain SYSTEM | Allow-list policy; no protected path is reachable whatever the caller asks |
| Another local user connects to the pipe | ACL plus caller image/signature verification |
| Replay of a captured privileged request | Per-connection sequence numbers; timestamp window |
| A rogue process impersonates the helper's pipe | Authenticated users are denied `CreateNewInstance` |
| A module writes something it cannot undo | `TransactionalStateAccessor` refuses undeclared writes; the transaction fails |
| A crash leaves the machine half-configured | Snapshot committed before any write; recovery at startup |
| A support bundle leaks personal data | Formatter-level redaction plus re-redaction during export |
| A "tweak" silently does nothing | Mandatory verification; a mismatch fails the step |

## Out of scope

The product does not defend against an attacker who is already administrator on the machine — at
that point the helper is not the weakest link. It also does not attempt anti-tampering or DRM;
invasive DRM is explicitly excluded from the initial architecture, and licensing is kept out of the
optimization engine so that no optimization decision can ever depend on a licence check.
