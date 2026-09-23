# 14. Frame capture and auto-tune

This is where the product's central claim is either earned or dropped.

## Frame capture, and why it can refuse

Windows exposes **no public API** that reports another process's frame times. The only documented
source is the kernel graphics ETW provider `Microsoft-Windows-DxgKrnl`, whose present events are
what PresentMon reads. Subscribing to it needs a real-time ETW session, which needs administrative
rights, and a session name can be held by only one process at a time.

That is a real constraint, so it is modelled rather than hidden. `IFrameTimeSource.GetStatusAsync`
returns why capture cannot start:

| State | Meaning |
| --- | --- |
| `Available` | Capture can run |
| `NotImplementedOnThisPlatform` | No capture implementation at all |
| `RequiresElevation` | The trace session needs administrator |
| `SessionAlreadyInUse` | Another process holds the session |

The Benchmark Lab shows that reason instead of producing a comparison with nothing behind it. The
registered default is `UnavailableFrameTimeSource`, so the lab is constructible everywhere and
answers honestly where capture cannot run.

**Frame times are presents, not renders.** These events record when a frame reached the compositor.
That is the right measurement for pacing and stutter and it is what every frame time tool on Windows
reports, but it is not the moment the GPU finished the frame, and the product does not say otherwise.

The ETW callback runs on the trace processing thread, so it writes one timestamp into a bounded
channel and returns. Under pressure the oldest frame is dropped rather than blocking that thread,
because blocking it loses events for every trace consumer on the machine.

## From presents to frame times

`FrameTimeRecorder` turns present timestamps into intervals: N presents yield N−1 frame times. Two
kinds of interval are discarded, **and both are counted**:

- a non-positive interval, meaning the trace delivered events out of order;
- an interval over one second, meaning the game was loading, paused or alt-tabbed.

Keeping either would put a spike in the tail the user never felt as stutter. Dropping them silently
would let a capture flatter itself, so the counts travel with the result.

## Statistics

Frame time, not frame rate, is the primary measurement: averaging frames per second hides exactly
the stutter this product exists to remove. Frame rate figures are derived from the frame time
aggregates for display.

The comparison uses:

- interpolated percentiles (P50, P95, P99, P99.9);
- the Mann-Whitney U test with tie and continuity corrections, which does not assume a normal
  distribution — frame time distributions are not normal;
- percentile bootstrap confidence intervals with a fixed seed, so a verdict is reproducible.

Three rules keep the verdict honest:

1. **Scope.** Runs are compared only within one hardware fingerprint and one workload. A result
   measured elsewhere is not evidence about this machine, and the repository has no query that
   crosses that boundary.
2. **No frames, no verdict.** A run without frame data is inconclusive, never a comparison of
   resource counters dressed up as frames.
3. **Significance is not importance.** A difference below the profile's threshold is reported as no
   difference however many frames were captured. With enough frames a 0.2% change becomes
   "statistically significant"; that is a fact about sample size, not a reason to change someone's
   machine.

And the rule that matters most in practice: **a tail regression vetoes the change.** "Average FPS up
1%, P99 frame time up 15%" is not a win, and the evaluator reverts it regardless of what the averages
did.

## Auto-tune

`AutoTuneEngine` measures a baseline, applies **one** candidate, measures again, and keeps it only if
the measurement says it helped.

- **One change per measurement.** Applying several and measuring once produces a number that cannot
  be attributed to anything.
- **Neutral is reverted.** Only a `Keep` verdict leaves a change applied. An unnecessary
  modification to someone's operating system is a cost even when it is free in frames.
- **The baseline is re-measured** before each candidate by default. A machine warms up, background
  work comes and goes, the player moves to a different area; a stale baseline turns drift into a
  result.
- **Nothing is concluded without frames.** Capture unavailable, or a measurement with no frames,
  means the candidate is reverted and recorded as not measured.
- **Restart-gated settings are excluded.** They cannot be measured in one sitting, so they are
  reverted and reported as such rather than measured against an unchanged machine.
- **The revert runs on a fresh token**, so a cancelled run still leaves the machine as it found it.

`AutoTunePlanner` admits a module only when it asks to be benchmarked, takes effect without a
restart, and is compatible with this machine. Candidates are ordered lowest risk first, so a run cut
short has still tried the safest changes.

## Trial results

Schema v2 stores one row per `(hardware fingerprint, workload, tweak, options hash)`. The scope is
the point: a trial result is evidence about the machine it was measured on and nothing else.

Options are hashed with their keys sorted first, so the same trial re-run accumulates evidence on one
row — `trial_count` rises — instead of creating a second row. "Hardware scheduling on" and "hardware
scheduling off" hash differently and are therefore distinct trials of the same module, which is what
makes a two-sided trial of one setting expressible.
