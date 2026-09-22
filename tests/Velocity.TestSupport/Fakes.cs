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
    public Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(DeclaredKeysOverride ?? new[] { Key });

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

/// <summary>A telemetry monitor that publishes whatever a test hands it.</summary>
public sealed class FakeSystemMonitor : Velocity.Abstractions.Telemetry.ISystemMonitor
{
    /// <inheritdoc />
    public Velocity.Abstractions.Telemetry.TelemetrySample? Latest { get; private set; }

    /// <inheritdoc />
    public Velocity.Abstractions.Telemetry.MonitorCadence Cadence { get; private set; }
        = Velocity.Abstractions.Telemetry.MonitorCadence.Stopped;

    /// <inheritdoc />
    public event EventHandler<Velocity.Abstractions.Telemetry.TelemetrySample>? SampleProduced;

    /// <summary>Number of times the monitor was started.</summary>
    public int StartCount { get; private set; }

    /// <summary>Publishes a sample to every subscriber.</summary>
    /// <param name="sample">Sample to publish.</param>
    public void Publish(Velocity.Abstractions.Telemetry.TelemetrySample sample)
    {
        Latest = sample;
        SampleProduced?.Invoke(this, sample);
    }

    /// <summary>Number of subscribers currently attached, used to assert clean disposal.</summary>
    public int SubscriberCount => SampleProduced?.GetInvocationList().Length ?? 0;

    /// <inheritdoc />
    public Task StartAsync(
        Velocity.Abstractions.Telemetry.MonitorCadence cadence,
        CancellationToken cancellationToken)
    {
        StartCount++;
        Cadence = cadence;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetCadence(Velocity.Abstractions.Telemetry.MonitorCadence cadence) => Cadence = cadence;

    /// <inheritdoc />
    public Task StopAsync()
    {
        Cadence = Velocity.Abstractions.Telemetry.MonitorCadence.Stopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An optimization engine that records what it was asked to do.</summary>
public sealed class FakeOptimizationEngine : Velocity.Core.Transactions.IOptimizationEngine
{
    /// <summary>Requests the engine received, in order.</summary>
    public List<Velocity.Core.Transactions.OptimizationRequest> Requests { get; } = new();

    /// <summary>Result the engine returns. Defaults to a successful empty run.</summary>
    public Velocity.Core.Transactions.OptimizationRunResult? Result { get; set; }

    /// <summary>Results returned from detection.</summary>
    public List<Velocity.Core.Transactions.TweakRunResult> DetectResults { get; } = new();

    /// <inheritdoc />
    public Task<Velocity.Core.Transactions.OptimizationRunResult> ApplyAsync(
        Velocity.Core.Transactions.OptimizationRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        return Task.FromResult(Result ?? new Velocity.Core.Transactions.OptimizationRunResult
        {
            TransactionId = Guid.NewGuid(),
            Status = Velocity.Abstractions.Transactions.TransactionStatus.Applied,
            Results = Array.Empty<Velocity.Core.Transactions.TweakRunResult>(),
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Velocity.Core.Transactions.TweakRunResult>> DetectAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Velocity.Core.Transactions.TweakRunResult>>(DetectResults);
}

/// <summary>A rollback engine that records what it was asked to undo.</summary>
public sealed class FakeRollbackEngine : Velocity.Core.Transactions.IRollbackEngine
{
    /// <summary>Transactions the engine was asked to roll back.</summary>
    public List<Guid> RolledBackTransactions { get; } = new();

    /// <summary>Tweaks the engine was asked to roll back.</summary>
    public List<string> RolledBackTweaks { get; } = new();

    /// <summary>Result returned for a rollback, or null to report nothing to do.</summary>
    public Velocity.Core.Transactions.RollbackResult? Result { get; set; }

    /// <inheritdoc />
    public Task<Velocity.Core.Transactions.RollbackResult> RollbackTransactionAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        RolledBackTransactions.Add(transactionId);
        return Task.FromResult(Result ?? new Velocity.Core.Transactions.RollbackResult(
            transactionId, 1, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    /// <inheritdoc />
    public Task<Velocity.Core.Transactions.RollbackResult?> RollbackLastAsync(
        CancellationToken cancellationToken) => Task.FromResult(Result);

    /// <inheritdoc />
    public Task<Velocity.Core.Transactions.RollbackResult?> RollbackTweakAsync(
        string tweakId,
        CancellationToken cancellationToken)
    {
        RolledBackTweaks.Add(tweakId);
        return Task.FromResult(Result);
    }
}

/// <summary>An applied-tweak reader returning a fixed map.</summary>
public sealed class FakeAppliedTweakReader : Velocity.Core.Transactions.IAppliedTweakReader
{
    private readonly Dictionary<string, string?> _values;

    /// <summary>Creates the reader.</summary>
    /// <param name="values">Original values keyed by tweak id.</param>
    public FakeAppliedTweakReader(Dictionary<string, string?>? values = null) =>
        _values = values ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string?>> GetAppliedOriginalValuesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>>(_values);
}
