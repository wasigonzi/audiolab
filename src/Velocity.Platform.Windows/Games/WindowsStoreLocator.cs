using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.Games;

namespace Velocity.Platform.Windows.Games;

/// <summary>
/// Finds where each store keeps its data, using the registry keys the stores themselves publish.
/// </summary>
/// <remarks>
/// <para>
/// Steam records its install path under <c>HKCU\Software\Valve\Steam!SteamPath</c>, with the
/// machine wide key as a fallback. Epic keeps its manifests in a fixed folder under
/// <c>ProgramData</c>. Both are read-only lookups; nothing here writes to a store's configuration.
/// </para>
/// <para>
/// <b>Stores that are not covered.</b> GOG Galaxy, EA, Ubisoft Connect and Battle.net keep their
/// installed-game records in private databases whose formats are undocumented and change between
/// client versions. Reading them by reverse engineering would produce a scanner that silently
/// breaks on the next client update, so this product does not claim to support them. Games from
/// those stores are added by the user instead, which is honest about what is being done.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsStoreLocator : IStoreLocator
{
    private const string SteamUserKey = @"Software\Valve\Steam";
    private const string SteamMachineKey = @"SOFTWARE\WOW6432Node\Valve\Steam";

    private readonly ILogger<WindowsStoreLocator> _logger;

    /// <summary>Creates the locator.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsStoreLocator(ILogger<WindowsStoreLocator> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetStoreRootsAsync(
        GameStore store,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> roots = store switch
        {
            GameStore.Steam => SteamRoots(),
            GameStore.Epic => EpicRoots(),
            _ => [],
        };

        return Task.FromResult(roots);
    }

    private IReadOnlyList<string> SteamRoots()
    {
        var roots = new List<string>();

        foreach (string? candidate in new[]
                 {
                     ReadString(Registry.CurrentUser, SteamUserKey, "SteamPath"),
                     ReadString(Registry.LocalMachine, SteamMachineKey, "InstallPath"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(candidate);
            }
        }

        if (roots.Count == 0)
        {
            _logger.LogDebug("Steam is not installed, or its install path is not in the registry.");
        }

        return roots;
    }

    private static IReadOnlyList<string> EpicRoots()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        if (string.IsNullOrWhiteSpace(programData))
        {
            return [];
        }

        return [Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")];
    }

    private string? ReadString(RegistryKey hive, string keyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not read {Key}!{Value}: {Reason}", keyPath, valueName, ex.Message);
            return null;
        }
    }
}
