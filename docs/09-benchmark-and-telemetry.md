# 9. Benchmark and telemetry architecture

## Position

The measurement data model, the statistics, the ETW frame-time capture, the recorder, the Benchmark
Lab and the auto-tune loop are all implemented. Nothing is stubbed: there is no fake sampler
returning plausible numbers, and where capture cannot run the product says so rather than producing
a comparison with nothing behind it.

The capture path has never run on Windows. See [11-status.md](11-status.md) for what that means, and
[14-frame-capture-and-autotune.md](14-frame-capture-and-autotune.md) for how a verdict is reached.

## Frame time, not frame rate

Everything is computed on the frame-time series; frame rates are derived for display. Averaging FPS
hides exactly the stutter the product exists to remove.

`FrameTimeStatistics` carries sample count, duration, mean, median, P95, P99, P99.9 and standard
deviation. `AverageFps`, `OnePercentLowFps` and `PointOnePercentLowFps` are computed properties —
the "1% low" is the frame-rate equivalent of the P99 frame time, stated that way so the definition
is not ambiguous.

Percentiles interpolate between order statistics. With 600 frames, a nearest-rank P99.9 can only
ever return the single worst frame, which makes the 0.1% low a function of sample size rather than
of the machine.

## Comparing two runs honestly

`SignificanceTest.MannWhitneyU` — a non-parametric rank test with tie correction and a continuity
correction, using the normal approximation (accurate for the hundreds-to-tens-of-thousands of
frames a benchmark produces).

Frame-time distributions are skewed and heavy-tailed, which is the whole reason tail percentiles
matter. A t-test assumes neither of those away safely; a rank test is not dragged around by a
handful of 80 ms frames.

`BootstrapMeanDifferenceInterval` and `BootstrapPercentileDifferenceInterval` produce percentile
bootstrap confidence intervals, so each metric — including each tail percentile — gets an interval
rather than two point estimates and a "bigger is better" comparison.

The bootstrap seed is **fixed**. A decision that changes between runs on identical data is not a
decision a user can trust. A test asserts reproducibility.

## The keep-or-revert rule

`TrialEvaluator` compares mean, median, P95, P99 and P99.9, then applies:

1. **Tail veto.** If P95, P99 or P99.9 credibly worsens by more than `TailRegressionVeto` (2% by
   default), the change is reverted **regardless of what the averages did**. This is the rule that
   stops "average FPS up 1%, P99 frame time up 15%" being reported as a win.
2. **Profile-specific decisive metrics.**
   - Competitive → P99, P99.9, median
   - Maximum FPS → mean, median
   - Balanced → mean, median, P99
3. **Effect-size floor.** A difference below `MinimumRelativeImprovement` (1%) is not kept even when
   it is statistically detectable. With enough frames a 0.2% difference becomes "significant"; that
   is a fact about sample size, not a reason to change someone's machine.
4. **Neutral means revert.** A change that measures as no different is reverted. An unnecessary
   modification to someone's operating system is a cost even when it is free in frames.
5. **Too few samples → `NeedsMoreData`**, not a guess.

Every comparison is returned in `TrialVerdict.Comparisons` with its baseline value, candidate value,
interval and verdict, so the UI can show the user the numbers behind the decision.

Tested in `TrialEvaluatorTests`, including the case the product exists to get right: the mean
improves, the tail gets much worse, and the verdict is `Revert` with "stutter regression" in the
rationale.

## Scoping

A `BenchmarkRun` records the hardware fingerprint, the workload, the transaction in effect and the
applied tweak ids. Results are only ever queried within one
`(hardware_fingerprint, workload_id)` pair, so history cannot silently mix a measurement taken
before a driver update with one taken after.

## Performance score

If a score is shown, it is derived transparently from the measured metrics above and the user can
open the measurements behind it. There is no opaque 0-100 number, and no score is computed from
"tweaks applied".

## Phase 9 plan

| Signal | Source |
| --- | --- |
| Frame times | ETW, `Microsoft-Windows-DxgKrnl` present events via TraceEvent |
| CPU utilization, per-core | PDH counters |
| Background CPU attribution | ETW process/thread CPU sampling, minus the measured game |
| GPU utilization, VRAM | PDH `GPU Engine` / `GPU Process Memory` counters |
| Disk activity | PDH `PhysicalDisk` counters |
| DPC/ISR | ETW kernel DPC/ISR events |
| Network latency and jitter | ICMP/UDP probes to a user-chosen endpoint |

Constraint carried into that phase: the optimizer must not become a source of stutter. Event-driven
where possible, no tight polling loops, log verbosity raised for the duration of a session
(`ILogVerbosityController`), and the collector's own overhead measured and reported.
