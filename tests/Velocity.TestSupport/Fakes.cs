using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;

namespace Velocity.TestSupport;

/// <summary>A profile provider that returns a fixture.</summary>
public sealed class FakeSystemProfileProvider : ISystemProfileProvider
{
    private SystemProfile _profile;

    /// <summary>Creates the provider.</summary>
    /// <param name="profile">Profile to return.</param>
    public FakeSystemProfileProvider(SystemProfile profile) => _profile = profile;

    /// <summary>Number of times a fresh capture was requested.</summary>
    public int RefreshCount { get; private set; }

    /// <inheritdoc />
    public Task<SystemProfile> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_profile);

    /// <inheritdoc />
    public Task<SystemProfile> RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshCount++;
        return Task.FromResult(_profile);
    }

    /// <summary>Replaces the profile that will be returned.</summary>
    /// <param name="profile">New profile.</param>
    public void SetProfile(SystemProfile profile) => _profile = profile;
}

/// <summary>A privilege context with a fixed answer.</summary>
/// <param name="Channel">Channel to report.</param>
public sealed record FakePrivilegeContext(PrivilegeChannel Channel = PrivilegeChannel.HelperService)
    : IPrivilegeContext
{
    /// <inheritdoc />
    public bool IsProcessElevated => Channel == PrivilegeChannel.DirectElevation;
}

/// <summary>
/// A configurable tweak used to drive the transaction pipeline in tests.
/// </summary>
/// <remarks>
/// It is a real <see cref="ITweak"/> exercising the real engine: the tests below apply it, verify
/// it, fail it and roll it back through exactly the code paths a production module will use.
/// </remarks>
public sealed class ScriptedTweak : ITweak, ICustomRollback
{
    private readonly Func<TweakContext, CancellationToken, Task<ApplyResult>> _apply;

    /// <summary>Creates a tweak that writes the given value to the given key.</summary>
    /// <param name="id">Tweak identifier.</param>
    /// <param name="key">Key the tweak writes.</param>
    /// <param name="targetValue">Value the tweak writes.</param>
    public ScriptedTweak(string id, StateKey key, StateValue targetValue)
        : this(id, key, async (context, token) =>
        {
            await context.State.WriteAsync(key, targetValue, token).ConfigureAwait(false);
            return ApplyResult.Applied($"Wrote {targetValue.ToDisplayString()}.", key);
        })
    {
        TargetValue = targetValue;
    }

    /// <summary>Creates a tweak with a custom apply implementation.</summary>
    /// <param name="id">Tweak identifier.</param>
    /// <param name="key">Key the tweak declares.</param>
    /// <param name="apply">Apply implementation.</param>
    public ScriptedTweak(
        string id,
        StateKey key,
        Func<TweakContext, CancellationToken, Task<ApplyResult>> apply)
    {
        Key = key;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));

        Descriptor = new TweakDescriptor
        {
            Id = id,
            Name = id,
            Category = TweakCategory.Cpu,
            Summary = "Test tweak.",
            TechnicalDescription = "Writes a value through the state accessor.",
            ExpectedEffect = "None; this module exists to exercise the engine.",
            Risk = RiskLevel.Safe,
            Scope = TweakScope.Session,
            RequiresElevation = false,
            RequiresRestart = false,
            BenchmarkRecommended = false,
        };
    }

    /// <summary>Key the tweak declares and writes.</summary>
    public StateKey Key { get; }

    /// <summary>Value the tweak writes, when it was created with the simple constructor.</summary>
    public StateValue TargetValue { get; }

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; set; }

    /// <summary>Compatibility result the tweak reports.</summary>
    public CompatibilityResult Compatibility { get; set; } = CompatibilityResult.Supported();

    /// <summary>Verification result the tweak reports.</summary>
    public VerificationResult Verification { get; set; } = VerificationResult.Verified();

    /// <summary>Whether a custom rollback has been invoked.</summary>
    public bool CustomRollbackInvoked { get; private set; }

    /// <summary>Payload handed back to the custom rollback.</summary>
    public string? ObservedRollbackPayload { get; private set; }

    /// <summary>Invoked when the custom rollback runs, so tests can observe ordering.</summary>
    public Action<string>? OnRollback { get; set; }

    /// <summary>Keys the tweak declares, overriding the single key it was created with.</summary>
    public IReadOnlyList<StateKey>? DeclaredKeysOverride { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<StateKey> GetStateKeys(TweakContext context) =>
        DeclaredKeysOverride ?? new[] { Key };

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken) => Task.FromResult(Compatibility);

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(TweakContext context, CancellationToken cancellationToken)
    {
        StateValue current = await context.State.ReadAsync(Key, cancellationToken).ConfigureAwait(false);

        return new TweakObservation
        {
            State = current.IsAbsent ? AppliedState.NotApplied : AppliedState.Applied,
            CurrentValueSummary = current.ToDisplayString(),
            RecommendedValueSummary = TargetValue.ToDisplayString(),
        };
    }

    /// <inheritdoc />
    public Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken) =>
        _apply(context, cancellationToken);

    /// <inheritdoc />
    public Task<VerificationResult> VerifyAsync(TweakContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Verification);

    /// <inheritdoc />
    public Task RollbackAsync(TweakContext context, string? rollbackPayload, CancellationToken cancellationToken)
    {
        CustomRollbackInvoked = true;
        ObservedRollbackPayload = rollbackPayload;
        OnRollback?.Invoke(Descriptor.Id);
        return Task.CompletedTask;
    }
}

/// <summary>A redactor that returns text unchanged, so tests can assert on exact values.</summary>
public sealed class PassThroughRedactor : Velocity.Abstractions.Diagnostics.ISensitiveDataRedactor
{
    /// <inheritdoc />
    public string Redact(string text) => text;
}
