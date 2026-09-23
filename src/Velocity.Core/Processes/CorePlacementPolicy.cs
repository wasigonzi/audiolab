using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Hardware;

namespace Velocity.Core.Processes;

/// <summary>What the caller is trying to achieve with core placement.</summary>
public enum PlacementIntent
{
    /// <summary>Do not constrain anything; report what would have been chosen.</summary>
    Observe = 0,

    /// <summary>
    /// Keep the game on the best cores and push background work elsewhere. Only proposed when the
    /// topology actually offers a better set of cores.
    /// </summary>
    IsolateGame = 1,

    /// <summary>
    /// Leave the game unconstrained and only push background work away from the best cores. Safer:
    /// it cannot make the game slower by confining it to too few cores.
    /// </summary>
    ConfineBackgroundOnly = 2,
}

/// <summary>The placement the policy proposes.</summary>
public sealed record PlacementPlan
{
    /// <summary>Global logical processor indexes the game may use.</summary>
    public required IReadOnlyList<int> GameProcessors { get; init; }

    /// <summary>Global logical processor indexes background work is confined to.</summary>
    public required IReadOnlyList<int> BackgroundProcessors { get; init; }

    /// <summary>Group relative affinity mask for background work, or <c>0</c> when not confining.</summary>
    public required ulong BackgroundAffinityMask { get; init; }

    /// <summary>Processor group the masks apply to.</summary>
    public required ushort GroupId { get; init; }

    /// <summary>Whether the game should be given a CPU set at all.</summary>
    public required bool ConstrainGame { get; init; }

    /// <summary>Whether background processes should be confined.</summary>
    public required bool ConstrainBackground { get; init; }

    /// <summary>Plain language explanation of the decision, shown in Expert Mode.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary><see langword="true"/> when the plan would change nothing.</summary>
    public bool IsNoOp => !ConstrainGame && !ConstrainBackground;
}

/// <summary>
/// Turns an interpreted processor layout into a concrete placement for a game and for background
/// work.
/// </summary>
/// <remarks>
/// <para>
/// The policy is conservative by construction. It refuses to confine a game unless the topology
/// gives a genuine reason (a hybrid part's performance cores, or one core complex with a larger
/// last level cache), and it refuses to confine background work unless enough cores are left for it
/// to run without queueing, because a background thread that cannot get scheduled produces the same
/// stutter by a different route.
/// </para>
/// <para>
/// Affinity masks are group relative, so a plan is only produced when the chosen cores live in one
/// processor group. On a machine with more than one group the policy declines and says so rather
/// than producing a mask that means something different from what it looks like.
/// </para>
/// </remarks>
public static class CorePlacementPolicy
{
    /// <summary>Fewest logical processors a confined game may be given.</summary>
    public const int MinimumGameProcessors = 6;

    /// <summary>Fewest logical processors background work may be confined to.</summary>
    public const int MinimumBackgroundProcessors = 4;

    /// <summary>Builds a placement plan.</summary>
    /// <param name="layout">Interpreted processor layout.</param>
    /// <param name="intent">What the caller wants.</param>
    /// <returns>The plan, which may be a no-op.</returns>
    public static PlacementPlan Create(CpuLayout layout, PlacementIntent intent)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var notes = new List<string>();
        CpuTopology topology = layout.Topology;

        IReadOnlyList<int> gameProcessors = ToLogicalProcessors(topology, layout.PreferredGameCoreIds);
        IReadOnlyList<int> backgroundProcessors = ToLogicalProcessors(topology, layout.BackgroundCoreIds);

        if (gameProcessors.Count == 0)
        {
            notes.Add("No processors could be resolved from the topology; placement is not attempted.");
            return NoOp(notes);
        }

        // A preferred set that is everything means the analyzer found no reason to constrain.
        bool topologyOffersAChoice = backgroundProcessors.Count > 0;

        if (!topologyOffersAChoice)
        {
            notes.Add(
                "This processor has no separate group of cores worth reserving, so neither the game " +
                "nor background work is confined.");
            return NoOp(notes);
        }

        if (!TryResolveGroup(topology, gameProcessors.Concat(backgroundProcessors), out ushort groupId))
        {
            notes.Add(
                "The chosen processors span more than one processor group. Affinity masks are group " +
                "relative, so no placement is proposed on this machine.");
            return NoOp(notes);
        }

        bool constrainGame = intent == PlacementIntent.IsolateGame &&
                             gameProcessors.Count >= MinimumGameProcessors;

        if (intent == PlacementIntent.IsolateGame && !constrainGame)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Only {gameProcessors.Count} logical processors would be left for the game, which is " +
                $"below the {MinimumGameProcessors} this policy will confine to. The game is left unconstrained."));
        }

        bool constrainBackground = intent is PlacementIntent.IsolateGame or PlacementIntent.ConfineBackgroundOnly &&
                                   backgroundProcessors.Count >= MinimumBackgroundProcessors;

        if (!constrainBackground && intent != PlacementIntent.Observe)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Only {backgroundProcessors.Count} logical processors are outside the preferred set, which is " +
                $"too few to confine background work to without it queueing. Background processes will be " +
                $"de-prioritised instead."));
        }

        if (constrainGame)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The game is offered {gameProcessors.Count} logical processors as a CPU set, which the " +
                $"scheduler may override under load."));
        }

        if (constrainBackground)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Background processes are confined to {backgroundProcessors.Count} logical processors by a " +
                $"hard affinity mask."));
        }

        return new PlacementPlan
        {
            GameProcessors = gameProcessors,
            BackgroundProcessors = backgroundProcessors,
            BackgroundAffinityMask = BuildMask(topology, backgroundProcessors),
            GroupId = groupId,
            ConstrainGame = constrainGame,
            ConstrainBackground = constrainBackground,
            Notes = notes,
        };
    }

    /// <summary>Builds a group relative affinity mask from global processor indexes.</summary>
    /// <param name="topology">Processor topology.</param>
    /// <param name="logicalProcessorIndexes">Global indexes to include.</param>
    /// <returns>The mask.</returns>
    public static ulong BuildMask(CpuTopology topology, IEnumerable<int> logicalProcessorIndexes)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(logicalProcessorIndexes);

        var byIndex = topology.LogicalProcessors.ToDictionary(processor => processor.GlobalIndex);
        ulong mask = 0;

        foreach (int index in logicalProcessorIndexes)
        {
            if (byIndex.TryGetValue(index, out LogicalProcessor? processor))
            {
                mask |= processor.AffinityMask;
            }
        }

        return mask;
    }

    private static PlacementPlan NoOp(IReadOnlyList<string> notes) => new()
    {
        GameProcessors = Array.Empty<int>(),
        BackgroundProcessors = Array.Empty<int>(),
        BackgroundAffinityMask = 0,
        GroupId = 0,
        ConstrainGame = false,
        ConstrainBackground = false,
        Notes = notes,
    };

    private static IReadOnlyList<int> ToLogicalProcessors(CpuTopology topology, IReadOnlyList<int> coreIds)
    {
        var wanted = coreIds.ToHashSet();

        return topology.PhysicalCores
            .Where(core => wanted.Contains(core.CoreId))
            .SelectMany(core => core.LogicalProcessorIndexes)
            .Order()
            .ToList();
    }

    private static bool TryResolveGroup(
        CpuTopology topology,
        IEnumerable<int> logicalProcessorIndexes,
        out ushort groupId)
    {
        var byIndex = topology.LogicalProcessors.ToDictionary(processor => processor.GlobalIndex);
        var groups = new HashSet<ushort>();

        foreach (int index in logicalProcessorIndexes)
        {
            if (byIndex.TryGetValue(index, out LogicalProcessor? processor))
            {
                groups.Add(processor.GroupId);
            }
        }

        groupId = groups.Count == 1 ? groups.First() : (ushort)0;
        return groups.Count == 1;
    }
}
