using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;

namespace Velocity.Core.AutoTune;

/// <summary>
/// Chooses which settings are worth putting through a measured trial.
/// </summary>
/// <remarks>
/// <para>
/// A tuning run costs the user real minutes per candidate, so the list is short on purpose. A
/// module is a candidate only when three things are true: it declares itself worth benchmarking, it
/// takes effect without a restart, and it is compatible with this machine. Everything else is
/// either not measurable in one sitting or not applicable, and putting it in the list would spend
/// the user's time to reach a foregone conclusion.
/// </para>
/// <para>
/// <b>Restart-gated settings are excluded, not fudged.</b> Hardware accelerated GPU scheduling is
/// the clearest example: it is exactly the kind of setting whose effect is worth measuring, and it
/// cannot be measured in one sitting. The planner leaves it out and the UI says why, rather than
/// measuring the unchanged machine and reporting a verdict from it.
/// </para>
/// </remarks>
public static class AutoTunePlanner
{
    /// <summary>Builds the candidate list for a machine.</summary>
    /// <param name="registry">Module catalogue.</param>
    /// <param name="contextFactory">Builds the context each compatibility check needs.</param>
    /// <param name="cancellationToken">Token used to abort planning.</param>
    /// <returns>The candidates, lowest risk first.</returns>
    public static async Task<IReadOnlyList<AutoTuneCandidate>> PlanAsync(
        ITweakRegistry registry,
        ITweakContextFactory contextFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(contextFactory);

        var candidates = new List<(AutoTuneCandidate Candidate, RiskLevel Risk)>();

        foreach (ITweak tweak in registry.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsTrialable(tweak.Descriptor))
            {
                continue;
            }

            (TweakContext context, _) = await contextFactory
                .CreateAsync(tweak, options: null, cancellationToken)
                .ConfigureAwait(false);

            CompatibilityResult compatibility = await tweak
                .CheckCompatibilityAsync(context, cancellationToken)
                .ConfigureAwait(false);

            if (!compatibility.IsSupported)
            {
                continue;
            }

            candidates.Add((
                new AutoTuneCandidate(
                    tweak.Descriptor.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    tweak.Descriptor.Name),
                tweak.Descriptor.Risk));
        }

        // Lowest risk first, so a run cut short has still tried the safest changes.
        return
        [
            .. candidates
                .OrderBy(entry => (int)entry.Risk)
                .ThenBy(entry => entry.Candidate.TweakId, StringComparer.Ordinal)
                .Select(entry => entry.Candidate)
        ];
    }

    /// <summary>Whether a module can be put through a single-sitting measured trial.</summary>
    /// <param name="descriptor">Module descriptor.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool IsTrialable(TweakDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // A restart-gated change cannot be measured before and after inside one run, and a module
        // that does not ask to be benchmarked is one whose effect is structural rather than
        // measurable in frames.
        return descriptor.BenchmarkRecommended && !descriptor.RequiresRestart;
    }
}
