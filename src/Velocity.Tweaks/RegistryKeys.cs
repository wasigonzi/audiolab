using Velocity.Abstractions.State;

namespace Velocity.Tweaks;

/// <summary>
/// Registry locations the modules use, in one place.
/// </summary>
/// <remarks>
/// Centralised so that every path a module can touch is visible in a single file and can be checked
/// against the privileged write allow list in <c>PrivilegedOperationPolicy</c> by reading two files
/// rather than the whole module set.
/// </remarks>
public static class RegistryKeys
{
    /// <summary>Scheme served by the registry state provider.</summary>
    public const string Scheme = "registry";

    /// <summary>Kernel scheduling quantum configuration.</summary>
    public const string PriorityControl = @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl";

    /// <summary>Graphics driver settings, including hardware accelerated GPU scheduling.</summary>
    public const string GraphicsDrivers = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    /// <summary>Multimedia Class Scheduler Service system wide settings.</summary>
    public const string MultimediaSystemProfile =
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    /// <summary>Multimedia Class Scheduler Service task settings for games.</summary>
    public const string MultimediaGamesTask =
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    /// <summary>Per user Game Bar settings.</summary>
    public const string GameBar = @"HKCU\Software\Microsoft\GameBar";

    /// <summary>Per user game configuration, including background recording.</summary>
    public const string GameConfigStore = @"HKCU\System\GameConfigStore";

    /// <summary>Per user graphics preferences, keyed by executable path.</summary>
    public const string UserGpuPreferences = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>TCP/IP stack parameters.</summary>
    public const string TcpipParameters = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    /// <summary>Network adapter class key; individual adapters are numbered subkeys.</summary>
    public const string NetworkAdapterClass =
        @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Builds a state key for a registry value.</summary>
    /// <param name="path">Key path including the hive.</param>
    /// <param name="valueName">Value name.</param>
    /// <returns>The state key.</returns>
    public static StateKey Value(string path, string valueName) => new(Scheme, path, valueName);
}
