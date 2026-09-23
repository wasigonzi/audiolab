using System;
using System.Collections.Generic;
using Velocity.Abstractions.Profiles;

namespace Velocity.Core.Profiles;

/// <summary>
/// The profiles the product ships with.
/// </summary>
/// <remarks>
/// <para>
/// A profile is a list of modules to offer, not a promise about frames. Each module still runs its
/// own compatibility check against the machine, so a profile naming a module the hardware does not
/// support results in that module being skipped with a reason, never in a blind write.
/// </para>
/// <para>
/// The three built-ins differ in what they are willing to trade, not in how many settings they
/// change. "More tweaks" is not "faster"; a profile that changes twenty things and cannot tell you
/// which one helped is the thing this product exists to replace.
/// </para>
/// </remarks>
public static class BuiltInProfiles
{
    /// <summary>Identifier of the competitive profile.</summary>
    public const string CompetitiveId = "builtin.competitive";

    /// <summary>Identifier of the maximum frame rate profile.</summary>
    public const string MaximumFpsId = "builtin.maximum-fps";

    /// <summary>Identifier of the balanced profile.</summary>
    public const string BalancedId = "builtin.balanced";

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Returns every built-in profile.</summary>
    /// <returns>The profiles.</returns>
    public static IReadOnlyList<OptimizationProfile> All() =>
    [
        Competitive(),
        MaximumFps(),
        Balanced(),
    ];

    /// <summary>
    /// Frame time consistency over average frame rate: the profile for a player who would rather
    /// have a steady 140 than a spiky 165.
    /// </summary>
    /// <returns>The profile.</returns>
    public static OptimizationProfile Competitive() => Create(
        CompetitiveId,
        "Competitive",
        ProfileKind.Competitive,
        [
            "cpu.scheduler-quantum",
            "cpu.background-priority",
            "cpu.game-core-placement",
            "windows.game-recording",
            "windows.session-service",
            "network.adapter-power-management",
            "network.interrupt-moderation",
            "power.performance-plan",
        ]);

    /// <summary>
    /// Average frame rate first. It omits the modules whose benefit is to consistency rather than
    /// throughput, and includes the GPU scheduling change, which is the one worth measuring.
    /// </summary>
    /// <returns>The profile.</returns>
    public static OptimizationProfile MaximumFps() => Create(
        MaximumFpsId,
        "Maximum frame rate",
        ProfileKind.MaximumFps,
        [
            "cpu.scheduler-quantum",
            "cpu.background-priority",
            "cpu.game-core-placement",
            "windows.game-recording",
            "power.performance-plan",
            "gpu.hardware-scheduling",
        ]);

    /// <summary>
    /// Session scoped changes only. Nothing here survives a restart, which is the profile to pick
    /// when the machine is not only used for games.
    /// </summary>
    /// <returns>The profile.</returns>
    public static OptimizationProfile Balanced() => Create(
        BalancedId,
        "Balanced",
        ProfileKind.Balanced,
        [
            "cpu.background-priority",
            "cpu.game-core-placement",
            "windows.game-recording",
            "power.performance-plan",
        ]);

    private static OptimizationProfile Create(
        string id,
        string name,
        ProfileKind kind,
        IReadOnlyList<string> tweakIds)
    {
        var settings = new List<ProfileTweakSetting>(tweakIds.Count);

        foreach (string tweakId in tweakIds)
        {
            settings.Add(new ProfileTweakSetting(
                tweakId,
                Enabled: true,
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return new OptimizationProfile
        {
            Id = id,
            Name = name,
            Kind = kind,
            Tweaks = settings,
            CreatedAtUtc = Epoch,
            ModifiedAtUtc = Epoch,
            IsBuiltIn = true,
        };
    }
}
