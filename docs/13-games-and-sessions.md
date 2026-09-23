# 13. Games and sessions

## Detection states its evidence

`GameDetector` never guesses. A running process is reported as a game for one of three reasons, and
the reason travels with the result so the UI can show it:

| Reason | Evidence |
| --- | --- |
| `InstalledLibraryMatch` | The process image is the executable named in an installed game's manifest |
| `StoreLibraryFolder` | The process lives inside a game's install directory |
| `UserDeclared` | The user marked this executable as a game |

`StoreLibraryFolder` is the weaker of the two automatic reasons and is labelled as such: a game's
install folder also holds its launcher, its crash reporter and its anti-cheat service.

**What is deliberately not done.** There is no heuristic based on GPU utilization, window style,
full-screen state or process name patterns. Those identify a video call, a browser playing video and
a 3D modelling tool as games. Acting on that guess means applying gaming optimizations to the wrong
process and attributing a benchmark to the wrong workload. Windows exposes no documented API that
answers "is this process a game", so where the evidence runs out the detector reports nothing.

Protected processes are never reported, whatever their path. A security agent installed under a
game's directory is still a security agent.

## The library

`GameLibrary` asks every registered `IGameLibrarySource` and merges the results. A source that
throws does not fail the scan: its failure is recorded in `GameLibraryScan.FailedSources` so the UI
can say "Epic could not be read" rather than silently showing a short library, which a user would
read as "my Epic games are not supported".

| Store | How it is read |
| --- | --- |
| Steam | `steamapps/libraryfolders.vdf` and `appmanifest_*.acf`, both layouts of the former |
| Epic | The launcher's JSON `.item` manifests under `ProgramData` |
| Everything else | Not scanned — added by the user |

**Why other stores are not scanned.** GOG Galaxy, EA, Ubisoft Connect and Battle.net keep their
installed-game records in private databases whose formats are undocumented and change between client
versions. A scanner built by reverse engineering them would break on the next update and would do so
silently. Saying "add those games by hand" is worse marketing and a better product.

Steam's manifest names the install directory but not the game binary. Guessing one — the largest
`.exe`, or the one matching the folder name — finds the anti-cheat service about as often as the
game, and a wrong executable means per-game settings applied to the wrong process. So the executable
is left unknown and the game is launched through `steam://rungameid/`, which is what the user's own
shortcut does.

Identifiers are derived only from things that do not move: `store:appid`, or a hash of the
normalised executable path. A display name is never part of an identifier, because a patch can
rename a title and a profile that loses its binding is worthless. The executable path is hashed
rather than stored, so a profile list cannot leak a Windows user name that appears in an install
path.

## Profiles

Three built-in profiles ship, and they differ in **what they are willing to trade**, not in how many
settings they change:

| Profile | Intent |
| --- | --- |
| Competitive | Frame time consistency and input latency over average frame rate |
| Maximum frame rate | Throughput, including the GPU scheduling change that is worth measuring |
| Balanced | Session-scoped changes only; nothing survives a restart |

Built-ins live in code, not in the database. A stored profile can never shadow a built-in one, which
removes the class of bug where a stale copy of "Competitive" names a module that no longer exists.

Per-game binding falls back to a configured default. With no default configured, nothing is
resolved and a session runs unoptimized — picking a profile the user never chose would change their
machine without being asked.

A profile apply is **not atomic**: one unsupported module must not cost the user the other seven,
so each is applied independently and each skip is reported with its reason.

## Optimize & Launch

`GamingSessionManager` runs: resolve profile → apply → launch → monitor → restore → report.

**The restore is unconditional.** It runs when the game exits, when the launch fails, when the wait
for the game times out, when the caller cancels, and when something throws. It lives in a `finally`
block and it runs on a fresh cancellation token, so cancelling a session cannot cancel the restore
half way. A restore that fails produces `CompletedWithRestoreFailures` — a distinct state, so the
product never reports a session as cleanly finished when it left the machine changed.

Two details that a naive implementation gets wrong:

- **A store launch returns the launcher, not the game.** `GameLaunchResult.IsGameProcess` says so,
  and the session then finds the game by detection. Waiting on the launcher's handle would report a
  two second play time and restore the machine while the user was still at the main menu.
- **Some titles relaunch themselves once at startup**, to switch renderer or to come up under their
  anti-cheat. The exit grace period stops the session ending seconds after it began.

A session report carries `ResourceStatistics` and, deliberately, no frame times. A session has no
before measurement, so it cannot support a statement about frame rate. That is the Benchmark Lab's
job.
