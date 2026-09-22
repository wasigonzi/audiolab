using System;
using System.Collections.Generic;

namespace Velocity.Abstractions.Profiles;

/// <summary>The intent behind a profile, which decides how conflicts are resolved.</summary>
public enum ProfileKind
{
    /// <summary>
    /// Prioritises frametime consistency, 1% lows and input responsiveness over average frame rate.
    /// When a change improves average FPS but worsens P99 frametime, this profile rejects it.
    /// </summary>
    Competitive = 0,

    /// <summary>Prioritises average frame rate.</summary>
    MaximumFps = 1,

    /// <summary>Improves gaming behaviour without persistent changes to Windows configuration.</summary>
    Balanced = 2,

    /// <summary>A user assembled set of tweaks.</summary>
    Custom = 3,
}

/// <summary>Per tweak settings inside a profile.</summary>
/// <param name="TweakId">Identifier of the tweak.</param>
/// <param name="Enabled">Whether the profile applies the tweak.</param>
/// <param name="Options">Option overrides passed to the tweak through the context.</param>
public sealed record ProfileTweakSetting(
    string TweakId,
    bool Enabled,
    IReadOnlyDictionary<string, string> Options);

/// <summary>A named set of optimizations, optionally bound to one game.</summary>
public sealed record OptimizationProfile
{
    /// <summary>Stable profile identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Intent behind the profile.</summary>
    public required ProfileKind Kind { get; init; }

    /// <summary>Game this profile is bound to, when it is game specific.</summary>
    public string? GameId { get; init; }

    /// <summary>Per tweak settings.</summary>
    public IReadOnlyList<ProfileTweakSetting> Tweaks { get; init; } = Array.Empty<ProfileTweakSetting>();

    /// <summary>When the profile was created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the profile was last modified.</summary>
    public required DateTimeOffset ModifiedAtUtc { get; init; }

    /// <summary><see langword="true"/> for profiles shipped with the product and not user editable.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Schema version of the serialized profile, for forward compatible imports.</summary>
    public int SchemaVersion { get; init; } = 1;
}
