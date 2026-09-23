using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Core.Tweaks;

/// <summary>One catalogue entry with its evaluated compatibility.</summary>
/// <param name="Tweak">The tweak.</param>
/// <param name="Compatibility">Result of evaluating it against the current machine.</param>
public readonly record struct CatalogueEntry(ITweak Tweak, CompatibilityResult Compatibility);

/// <summary>The catalogue of optimization modules available in this build.</summary>
public interface ITweakRegistry
{
    /// <summary>Every registered tweak, ordered by category then name.</summary>
    IReadOnlyList<ITweak> All { get; }

    /// <summary>Looks a tweak up by identifier.</summary>
    /// <param name="tweakId">Identifier to look up.</param>
    /// <returns>The tweak, or <see langword="null"/> when this build does not contain it.</returns>
    ITweak? Find(string tweakId);

    /// <summary>Returns the tweaks in one category.</summary>
    /// <param name="category">Category to filter by.</param>
    /// <returns>The tweaks in that category.</returns>
    IReadOnlyList<ITweak> InCategory(TweakCategory category);

    /// <summary>
    /// Evaluates every tweak against the current machine, applying both the declarative
    /// requirements and each tweak's own compatibility check.
    /// </summary>
    /// <param name="context">Execution context describing the machine.</param>
    /// <param name="cancellationToken">Token used to abort the evaluation.</param>
    /// <returns>The catalogue with per tweak compatibility.</returns>
    Task<IReadOnlyList<CatalogueEntry>> EvaluateAsync(TweakContext context, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="ITweakRegistry"/>.</summary>
public sealed class TweakRegistry : ITweakRegistry
{
    private readonly Dictionary<string, ITweak> _byId;

    /// <summary>Creates the registry.</summary>
    /// <param name="tweaks">Tweaks registered in the container.</param>
    /// <exception cref="ArgumentException">Two tweaks share an identifier.</exception>
    public TweakRegistry(IEnumerable<ITweak> tweaks)
    {
        ArgumentNullException.ThrowIfNull(tweaks);

        _byId = new Dictionary<string, ITweak>(StringComparer.OrdinalIgnoreCase);
        foreach (ITweak tweak in tweaks)
        {
            if (!_byId.TryAdd(tweak.Descriptor.Id, tweak))
            {
                throw new ArgumentException(
                    $"More than one tweak declares the id '{tweak.Descriptor.Id}'. Ids are used as " +
                    "rollback and benchmark keys and must be unique.",
                    nameof(tweaks));
            }
        }

        All = new ReadOnlyCollection<ITweak>(
            _byId.Values
                .OrderBy(tweak => tweak.Descriptor.Category)
                .ThenBy(tweak => tweak.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <inheritdoc />
    public IReadOnlyList<ITweak> All { get; }

    /// <inheritdoc />
    public ITweak? Find(string tweakId) =>
        !string.IsNullOrWhiteSpace(tweakId) && _byId.TryGetValue(tweakId, out ITweak? tweak) ? tweak : null;

    /// <inheritdoc />
    public IReadOnlyList<ITweak> InCategory(TweakCategory category) =>
        All.Where(tweak => tweak.Descriptor.Category == category).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogueEntry>> EvaluateAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var entries = new List<CatalogueEntry>(All.Count);

        foreach (ITweak tweak in All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CompatibilityResult declared = CompatibilityEvaluator.Evaluate(
                tweak.Descriptor, context.Profile, context.CpuLayout, context.Privileges);

            if (!declared.IsSupported)
            {
                entries.Add(new CatalogueEntry(tweak, declared));
                continue;
            }

            CompatibilityResult specific;
            try
            {
                specific = await tweak.CheckCompatibilityAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A tweak that cannot decide is not offered. Failing closed here is the difference
                // between "we could not tell" and "we assumed it was fine".
                context.Logger.LogTweakCompatibilityFailure(tweak.Descriptor.Id, ex);
                specific = CompatibilityResult.Unsupported(
                    CompatibilityStatus.Unknown,
                    $"Compatibility could not be determined: {ex.Message}");
            }

            entries.Add(new CatalogueEntry(tweak, specific));
        }

        return entries;
    }
}
