using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Data.Repositories;

namespace Velocity.Core.Games;

/// <summary>
/// Stores the executables the user marked as games, in the application settings table.
/// </summary>
/// <remarks>
/// Only the executable file name is stored, never the full path. A path leaks a Windows user name
/// often enough that keeping it out of the database costs nothing and removes the question, and the
/// file name is what the detector matches on anyway.
/// </remarks>
public sealed class UserDeclaredGames : IUserDeclaredGames
{
    /// <summary>Settings key holding the declared executables.</summary>
    public const string SettingKey = "games.user-declared";

    private readonly ISettingsRepository _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    /// <param name="settings">Application settings storage.</param>
    public UserDeclaredGames(ISettingsRepository settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetDeclaredExecutablesAsync(CancellationToken cancellationToken)
    {
        string? stored = await _settings.GetAsync(SettingKey, cancellationToken).ConfigureAwait(false);
        return Parse(stored);
    }

    /// <inheritdoc />
    public async Task DeclareAsync(
        string executableName,
        bool isGame,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        string name = GameIdentity.FileName(executableName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string? stored = await _settings.GetAsync(SettingKey, cancellationToken).ConfigureAwait(false);
            var declared = new HashSet<string>(Parse(stored), StringComparer.OrdinalIgnoreCase);

            if (isGame)
            {
                declared.Add(name);
            }
            else
            {
                declared.Remove(name);
            }

            await _settings
                .SetAsync(
                    SettingKey,
                    JsonSerializer.Serialize(declared.OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlySet<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            string[]? names = JsonSerializer.Deserialize<string[]>(stored);
            return names is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A corrupt setting must not stop detection; the user can mark their games again.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
