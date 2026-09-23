using System;
using System.Collections.Generic;
using Velocity.Abstractions.Hardware;

namespace Velocity.Abstractions.Tweaks;

/// <summary>
/// Declarative hardware and platform preconditions, used to filter the catalogue before any tweak
/// code runs so that the UI never offers a setting the machine cannot honour.
/// </summary>
public sealed record HardwareRequirements
{
    /// <summary>CPU vendors the tweak applies to. Empty means any vendor.</summary>
    public IReadOnlyList<CpuVendor> CpuVendors { get; init; } = Array.Empty<CpuVendor>();

    /// <summary>GPU vendors the tweak applies to. Empty means any vendor.</summary>
    public IReadOnlyList<GpuVendor> GpuVendors { get; init; } = Array.Empty<GpuVendor>();

    /// <summary>Minimum number of physical cores required.</summary>
    public int MinimumPhysicalCores { get; init; }

    /// <summary>When set, the tweak requires (or requires the absence of) a hybrid processor.</summary>
    public bool? RequiresHybridCpu { get; init; }

    /// <summary>When set, the tweak requires more than one core complex to be present.</summary>
    public bool? RequiresMultipleCoreComplexes { get; init; }

    /// <summary>Machine kinds the tweak must not run on, for example laptops on battery.</summary>
    public IReadOnlyList<MachineKind> ExcludedMachineKinds { get; init; } = Array.Empty<MachineKind>();

    /// <summary>A requirement set that matches every machine.</summary>
    public static HardwareRequirements None { get; } = new();
}

/// <summary>
/// Everything the UI, the catalogue and the licensing layer need to know about a tweak without
/// executing it.
/// </summary>
/// <remarks>
/// Descriptors are data. They are deliberately separable from the implementation so that a later
/// release can ship remote tweak definitions and a hardware database keyed on
/// <see cref="Id"/> and <see cref="DefinitionVersion"/>.
/// </remarks>
public sealed record TweakDescriptor
{
    /// <summary>
    /// Stable identifier, for example <c>cpu.game-process-affinity</c>. Never reused for a
    /// different meaning: rollback records and benchmark history are keyed on it.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Short display name.</summary>
    public required string Name { get; init; }

    /// <summary>Category the tweak appears under.</summary>
    public required TweakCategory Category { get; init; }

    /// <summary>One sentence explanation written for a normal gamer.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Full technical explanation: which component it affects, which API or setting controls it,
    /// what the Windows default is, and what is expected to change. Shown in Expert Mode.
    /// </summary>
    public required string TechnicalDescription { get; init; }

    /// <summary>
    /// What the user should expect. Written honestly, including "no measurable effect on many
    /// systems" where that is the truth.
    /// </summary>
    public required string ExpectedEffect { get; init; }

    /// <summary>Risk classification.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>Whether the change is session scoped or persistent.</summary>
    public required TweakScope Scope { get; init; }

    /// <summary>Whether the privileged helper is needed to apply it.</summary>
    public required bool RequiresElevation { get; init; }

    /// <summary>Whether a restart is required before the change takes effect.</summary>
    public required bool RequiresRestart { get; init; }

    /// <summary>Whether the auto-tune engine should measure this tweak rather than assume it helps.</summary>
    public required bool BenchmarkRecommended { get; init; }

    /// <summary>Lowest Windows build the tweak is known to work on.</summary>
    public int MinimumWindowsBuild { get; init; } = 19041;

    /// <summary>Highest Windows build the tweak is known to work on, when it was removed later.</summary>
    public int? MaximumWindowsBuild { get; init; }

    /// <summary>Declarative hardware preconditions.</summary>
    public HardwareRequirements Hardware { get; init; } = HardwareRequirements.None;

    /// <summary>Version of this definition. Incremented when behaviour or state keys change.</summary>
    public int DefinitionVersion { get; init; } = 1;

    /// <summary>Documentation link shown in Expert Mode.</summary>
    public Uri? Documentation { get; init; }
}
