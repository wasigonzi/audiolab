using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.State;

namespace Velocity.Core.Transactions;

/// <summary>Builds the execution context a tweak runs in.</summary>
public interface ITweakContextFactory
{
    /// <summary>
    /// Creates a context whose state accessor permits writes to exactly the keys the tweak
    /// declared for this machine.
    /// </summary>
    /// <param name="tweak">Tweak the context is for.</param>
    /// <param name="options">Profile supplied options.</param>
    /// <param name="cancellationToken">Token used to abort the capture of machine state.</param>
    /// <returns>The context and the accessor that records its writes.</returns>
    Task<(TweakContext Context, TransactionalStateAccessor Accessor)> CreateAsync(
        ITweak tweak,
        IReadOnlyDictionary<string, string>? options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a context whose state accessor permits writes to an explicit key set. Used by the
    /// rollback engine, which must write back exactly the keys a snapshot holds and nothing else.
    /// </summary>
    /// <param name="tweakId">Tweak the context is attributed to.</param>
    /// <param name="keys">Keys the context may write.</param>
    /// <param name="cancellationToken">Token used to abort the capture of machine state.</param>
    /// <returns>The context and the accessor that records its writes.</returns>
    Task<(TweakContext Context, TransactionalStateAccessor Accessor)> CreateForKeysAsync(
        string tweakId,
        IEnumerable<StateKey> keys,
        CancellationToken cancellationToken);

    /// <summary>Returns the interpreted processor layout for the current machine.</summary>
    /// <param name="cancellationToken">Token used to abort the capture of machine state.</param>
    /// <returns>The layout.</returns>
    Task<CpuLayout> GetCpuLayoutAsync(CancellationToken cancellationToken);
}

/// <summary>Default <see cref="ITweakContextFactory"/>.</summary>
public sealed class TweakContextFactory : ITweakContextFactory
{
    private readonly ISystemProfileProvider _profileProvider;
    private readonly IStateProviderRegistry _stateProviders;
    private readonly IPrivilegeContext _privileges;
    private readonly ILoggerFactory _loggerFactory;

    private SystemProfile? _layoutSource;
    private CpuLayout? _layout;

    /// <summary>Creates the factory.</summary>
    /// <param name="profileProvider">Source of the current machine profile.</param>
    /// <param name="stateProviders">State providers available on this platform.</param>
    /// <param name="privileges">Privileges available to the current process.</param>
    /// <param name="loggerFactory">Logger factory used to create per tweak loggers.</param>
    public TweakContextFactory(
        ISystemProfileProvider profileProvider,
        IStateProviderRegistry stateProviders,
        IPrivilegeContext privileges,
        ILoggerFactory loggerFactory)
    {
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _stateProviders = stateProviders ?? throw new ArgumentNullException(nameof(stateProviders));
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public async Task<(TweakContext Context, TransactionalStateAccessor Accessor)> CreateAsync(
        ITweak tweak,
        IReadOnlyDictionary<string, string>? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tweak);

        SystemProfile profile = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        CpuLayout layout = GetOrCreateLayout(profile);

        // The declared key set depends on the machine, so a probe context is built first and the
        // real accessor is created from the keys that probe context reports.
        var probeAccessor = new TransactionalStateAccessor(
            _stateProviders, _privileges, tweak.Descriptor.Id, Array.Empty<StateKey>());
        var probeContext = new TweakContext(
            profile, layout, probeAccessor, _privileges, CreateLogger(tweak.Descriptor.Id), options);

        IReadOnlyList<StateKey> declaredKeys = tweak.GetStateKeys(probeContext);

        var accessor = new TransactionalStateAccessor(
            _stateProviders, _privileges, tweak.Descriptor.Id, declaredKeys);
        var context = new TweakContext(
            profile, layout, accessor, _privileges, CreateLogger(tweak.Descriptor.Id), options);

        return (context, accessor);
    }

    /// <inheritdoc />
    public async Task<(TweakContext Context, TransactionalStateAccessor Accessor)> CreateForKeysAsync(
        string tweakId,
        IEnumerable<StateKey> keys,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);
        ArgumentNullException.ThrowIfNull(keys);

        SystemProfile profile = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        CpuLayout layout = GetOrCreateLayout(profile);

        var accessor = new TransactionalStateAccessor(_stateProviders, _privileges, tweakId, keys);
        var context = new TweakContext(profile, layout, accessor, _privileges, CreateLogger(tweakId));

        return (context, accessor);
    }

    /// <inheritdoc />
    public async Task<CpuLayout> GetCpuLayoutAsync(CancellationToken cancellationToken)
    {
        SystemProfile profile = await _profileProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return GetOrCreateLayout(profile);
    }

    private CpuLayout GetOrCreateLayout(SystemProfile profile)
    {
        // Analysis is pure and cheap, but it runs for every tweak in the catalogue during a
        // detection sweep, so the result is memoised against the profile it came from.
        if (!ReferenceEquals(_layoutSource, profile) || _layout is null)
        {
            _layout = CpuTopologyAnalyzer.Analyze(profile.Cpu);
            _layoutSource = profile;
        }

        return _layout;
    }

    private ILogger CreateLogger(string tweakId) => _loggerFactory.CreateLogger($"Velocity.Tweak.{tweakId}");
}
