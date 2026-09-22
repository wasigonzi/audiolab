using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Profiles;
using Velocity.Core.Transactions;
using Velocity.Data.Repositories;

namespace Velocity.Core.Profiles;

/// <summary>Stores, resolves and applies optimization profiles.</summary>
public interface IProfileService
{
    /// <summary>Returns the built-in profiles and the user's own, built-ins first.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The profiles.</returns>
    Task<IReadOnlyList<OptimizationProfile>> GetProfilesAsync(CancellationToken cancellationToken);

    /// <summary>Returns one profile by identifier.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The profile, or <see langword="null"/>.</returns>
    Task<OptimizationProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the profile to use for a game: the one bound to it, or the configured default.
    /// </summary>
    /// <param name="gameId">Game identifier, or <see langword="null"/> for no particular game.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The profile to apply, or <see langword="null"/> when none is configured.</returns>
    Task<OptimizationProfile?> ResolveForGameAsync(string? gameId, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a user profile.</summary>
    /// <param name="profile">Profile to store.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the profile is stored.</returns>
    /// <exception cref="InvalidOperationException">The profile is marked built-in.</exception>
    Task SaveAsync(OptimizationProfile profile, CancellationToken cancellationToken);

    /// <summary>Deletes a user profile. Built-in profiles cannot be deleted.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns><see langword="true"/> when a profile was removed.</returns>
    Task<bool> DeleteAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>Sets the profile applied when no game specific profile exists.</summary>
    /// <param name="profileId">Profile identifier, or <see langword="null"/> to apply none.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the setting is stored.</returns>
    Task SetDefaultProfileAsync(string? profileId, CancellationToken cancellationToken);

    /// <summary>Builds the engine request that applies a profile.</summary>
    /// <param name="profile">Profile to apply.</param>
    /// <param name="reason">Why the transaction is being opened.</param>
    /// <param name="sessionId">Gaming session the transaction belongs to, when there is one.</param>
    /// <returns>The request.</returns>
    OptimizationRequest BuildRequest(
        OptimizationProfile profile,
        Abstractions.Transactions.TransactionReason reason,
        Guid? sessionId = null);
}

/// <summary>
/// The profile store, layered over the built-in profiles.
/// </summary>
/// <remarks>
/// <para>
/// Built-in profiles are not written to the database. Keeping them in code means a product update
/// that improves a built-in profile reaches every user, and it removes the class of bug where a
/// stale stored copy of "Competitive" names a module that no longer exists.
/// </para>
/// <para>
/// A profile apply is never atomic. One module being unavailable on this machine must not cost the
/// user the other seven, so <see cref="BuildRequest"/> sets
/// <see cref="OptimizationRequest.AtomicAllOrNothing"/> to false and each skipped module is
/// reported with its reason.
/// </para>
/// </remarks>
public sealed class ProfileService : IProfileService
{
    /// <summary>Settings key holding the default profile identifier.</summary>
    public const string DefaultProfileSettingKey = "profiles.default";

    private readonly IProfileRepository _repository;
    private readonly ISettingsRepository _settings;
    private readonly ILogger<ProfileService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="repository">Profile storage.</param>
    /// <param name="settings">Application settings storage.</param>
    /// <param name="logger">Logger.</param>
    public ProfileService(
        IProfileRepository repository,
        ISettingsRepository settings,
        ILogger<ProfileService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OptimizationProfile>> GetProfilesAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OptimizationProfile> stored =
            await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var profiles = new List<OptimizationProfile>(BuiltInProfiles.All());

        // A stored profile never shadows a built-in one: the built-in definition is authoritative.
        profiles.AddRange(stored.Where(profile => !profile.IsBuiltIn &&
            !profiles.Any(existing => string.Equals(existing.Id, profile.Id, StringComparison.Ordinal))));

        return profiles;
    }

    /// <inheritdoc />
    public async Task<OptimizationProfile?> GetProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        OptimizationProfile? builtIn = BuiltInProfiles.All()
            .FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));

        return builtIn ?? await _repository.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OptimizationProfile?> ResolveForGameAsync(
        string? gameId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            IReadOnlyList<OptimizationProfile> stored =
                await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);

            OptimizationProfile? bound = stored.FirstOrDefault(profile =>
                string.Equals(profile.GameId, gameId, StringComparison.OrdinalIgnoreCase));

            if (bound is not null)
            {
                _logger.LogDebug("Resolved profile {Profile} bound to game {Game}.", bound.Id, gameId);
                return bound;
            }
        }

        string? defaultId = await _settings
            .GetAsync(DefaultProfileSettingKey, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(defaultId)
            ? null
            : await GetProfileAsync(defaultId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SaveAsync(OptimizationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.IsBuiltIn)
        {
            throw new InvalidOperationException(
                $"'{profile.Id}' is a built-in profile. Copy it to a new profile instead of editing it.");
        }

        return _repository.UpsertAsync(profile, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string profileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        bool isBuiltIn = BuiltInProfiles.All()
            .Any(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));

        return isBuiltIn
            ? Task.FromResult(false)
            : _repository.DeleteAsync(profileId, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetDefaultProfileAsync(string? profileId, CancellationToken cancellationToken) =>
        _settings.SetAsync(DefaultProfileSettingKey, profileId ?? string.Empty, cancellationToken);

    /// <inheritdoc />
    public OptimizationRequest BuildRequest(
        OptimizationProfile profile,
        Abstractions.Transactions.TransactionReason reason,
        Guid? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ProfileTweakSetting[] enabled = [.. profile.Tweaks.Where(setting => setting.Enabled)];

        var options = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (ProfileTweakSetting setting in enabled.Where(setting => setting.Options.Count > 0))
        {
            options[setting.TweakId] = setting.Options;
        }

        return new OptimizationRequest
        {
            TweakIds = [.. enabled.Select(setting => setting.TweakId)],
            Reason = reason,
            ProfileId = profile.Id,
            SessionId = sessionId,
            Options = options,

            // One unsupported module must not cost the user the rest of the profile.
            AtomicAllOrNothing = false,
        };
    }
}
