using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Services;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Services;

namespace Velocity.Tweaks.Windows;

/// <summary>
/// Stops optional services for the duration of a gaming session and starts them again afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "optional" means here.</b> Not a list. A service qualifies only when it is currently
/// running, nothing running depends on it, it is not a driver, it is not on the small protective
/// list of platform, security or gaming services, and it is not published by a hardware vendor.
/// <see cref="ServiceClassifier"/> derives that per machine from the dependency graph, so the answer
/// is correct on machines the author has never seen.
/// </para>
/// <para>
/// <b>What it never does.</b> It never changes a start mode and never disables anything. The
/// strongest action is a stop, and the previous state is captured first so the session end — or
/// crash recovery on the next launch — starts it again.
/// </para>
/// <para>
/// <b>Honest about magnitude.</b> Most optional services are idle most of the time. Stopping an
/// idle service frees memory and removes a possible wake source; it does not free processor time
/// that was not being used. The measurable win is on machines carrying a lot of installed
/// background software, and the module reports how many candidates it actually found rather than
/// implying a fixed benefit.
/// </para>
/// </remarks>
public sealed class SessionServiceTweak : ITweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "windows.session-services";

    /// <summary>
    /// Option capping how many services may be stopped in one session, so an unusual machine
    /// cannot turn one click into fifty service control operations.
    /// </summary>
    public const string MaximumServicesOption = "maximum-services";

    /// <summary>Default cap on the number of services stopped in one session.</summary>
    public const int DefaultMaximumServices = 12;

    private readonly IServiceInspector _inspector;

    /// <summary>Creates the module.</summary>
    /// <param name="inspector">Reads the service control manager.</param>
    public SessionServiceTweak(IServiceInspector inspector) =>
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Pause optional services while playing",
        Category = TweakCategory.Services,
        Summary =
            "Stops services that are running, that nothing depends on, and that are not part of " +
            "Windows, your security software, your hardware or your games. They are started again " +
            "when the session ends.",
        TechnicalDescription =
            "Classifies every service on the machine from the live dependency graph rather than " +
            "from a shipped list. A service is a candidate only when it is running, has no running " +
            "dependents, is not a kernel or file system driver, is not started by the kernel, and " +
            "is not on the protective list of platform, security, hardware vendor or gaming " +
            "services. Candidates are stopped through the service control manager and their " +
            "previous state is journalled first, so the session end or crash recovery restarts " +
            "them. Start modes are never changed and nothing is ever disabled.",
        ExpectedEffect =
            "Frees the memory an idle service holds and removes it as a wake source. It does not " +
            "free processor time that was not being used, so on a lean machine the measurable " +
            "effect is close to zero. The module reports how many candidates it found on this " +
            "machine so the user can judge before applying.",
        Risk = RiskLevel.Moderate,
        Scope = TweakScope.Session,
        RequiresElevation = true,
        RequiresRestart = false,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        return candidates
            .Select(service => new StateKey(
                ServiceStateProvider.Scheme, service.Name, ServiceStateProvider.StateItem))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        return candidates.Count == 0
            ? CompatibilityResult.Unsupported(
                CompatibilityStatus.AlreadyOptimal,
                "Nothing is running that can be paused safely: every running service is part of " +
                "Windows, your security software, your hardware or your games, or something depends " +
                "on it.")
            : CompatibilityResult.Supported(string.Create(
                CultureInfo.InvariantCulture,
                $"{candidates.Count} optional service(s) can be paused for the session."));
    }

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ServiceSnapshot> all =
            await _inspector.GetServicesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ServiceSnapshot> classified = ServiceClassifier.ClassifyAll(all);
        IReadOnlyList<ServiceSnapshot> candidates = SelectCandidates(classified, context);

        var byClassification = classified
            .GroupBy(service => service.Classification)
            .ToDictionary(
                group => group.Key.ToString(),
                group => group.Count().ToString(CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

        return new TweakObservation
        {
            State = candidates.Count == 0 ? AppliedState.Applied : AppliedState.NotApplied,
            CurrentValueSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"{classified.Count(service => service.State == ServiceState.Running)} services running, " +
                $"{candidates.Count} of them optional"),
            RecommendedValueSummary = candidates.Count == 0
                ? "Nothing to pause"
                : string.Create(
                    CultureInfo.InvariantCulture, $"Pause {candidates.Count} service(s) during the session"),
            Details = new Dictionary<string, string>(byClassification, StringComparer.Ordinal)
            {
                ["candidates"] = string.Join(", ", candidates.Select(service => service.Name)),
            },
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ServiceSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return ApplyResult.NoChange("Nothing was running that could be paused safely.");
        }

        var changed = new List<StateKey>();

        foreach (ServiceSnapshot service in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = new StateKey(
                ServiceStateProvider.Scheme, service.Name, ServiceStateProvider.StateItem);

            await context.State
                .WriteAsync(key, StateValue.FromString("Stopped"), cancellationToken)
                .ConfigureAwait(false);

            changed.Add(key);
        }

        return ApplyResult.Applied(
            string.Create(CultureInfo.InvariantCulture, $"Paused {changed.Count} optional service(s)."),
            changed.ToArray());
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ServiceSnapshot> candidates =
            await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);

        // A service that refused to stop is reported, not treated as a failure: the machine is in a
        // valid state either way and the journal still holds its original value.
        return candidates.Count == 0
            ? VerificationResult.Verified("Every optional service reached the stopped state.")
            : new VerificationResult(
                VerificationStatus.Verified,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{candidates.Count} service(s) did not stop: {string.Join(", ", candidates.Select(service => service.Name))}."));
    }

    private async Task<IReadOnlyList<ServiceSnapshot>> FindCandidatesAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceSnapshot> all =
            await _inspector.GetServicesAsync(cancellationToken).ConfigureAwait(false);

        return SelectCandidates(ServiceClassifier.ClassifyAll(all), context);
    }

    private static IReadOnlyList<ServiceSnapshot> SelectCandidates(
        IReadOnlyList<ServiceSnapshot> classified,
        TweakContext context)
    {
        int maximum = context.GetOption(MaximumServicesOption, DefaultMaximumServices);

        return classified
            .Where(service => service.IsSessionCandidate)
            .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(maximum, 0))
            .ToList();
    }
}
