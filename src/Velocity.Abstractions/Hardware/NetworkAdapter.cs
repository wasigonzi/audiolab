using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>Physical medium of a network adapter.</summary>
public enum NetworkInterfaceKind
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>Wired Ethernet.</summary>
    Ethernet = 1,

    /// <summary>IEEE 802.11 wireless.</summary>
    Wireless = 2,

    /// <summary>Loopback interface.</summary>
    Loopback = 3,

    /// <summary>Virtual or tunnel adapter (Hyper-V switch, VPN, ...).</summary>
    Virtual = 4,
}

/// <summary>
/// Hardware capabilities of a network adapter, read from the miniport keyword set the driver
/// publishes. Every value is nullable because the keyword may not exist on a given driver, and
/// "the driver does not expose this" must never be presented to the user as "off".
/// </summary>
public sealed record NetworkAdapterCapabilities
{
    /// <summary>Whether Receive Side Scaling is exposed and enabled.</summary>
    public bool? ReceiveSideScalingEnabled { get; init; }

    /// <summary>Number of RSS queues configured, when exposed.</summary>
    public int? ReceiveSideScalingQueues { get; init; }

    /// <summary>Whether the driver exposes an interrupt moderation keyword.</summary>
    public bool? InterruptModerationSupported { get; init; }

    /// <summary>Whether interrupt moderation is currently enabled.</summary>
    public bool? InterruptModerationEnabled { get; init; }

    /// <summary>
    /// Whether Windows is allowed to power the adapter down.
    /// </summary>
    /// <remarks>
    /// Only two values of <c>PnPCapabilities</c> are documented well enough to interpret: absent or
    /// zero means power management is permitted, and 24 means both power management checkboxes are
    /// cleared. Any other value is reported as unknown rather than guessed at from its bits.
    /// </remarks>
    public bool? PowerManagementEnabled { get; init; }

    /// <summary>Whether Energy Efficient Ethernet is exposed and enabled.</summary>
    public bool? EnergyEfficientEthernetEnabled { get; init; }

    /// <summary>
    /// Raw miniport keywords and their current values, so Expert Mode can show exactly what the
    /// driver exposes instead of a curated subset.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdvancedProperties { get; init; }
        = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
}

/// <summary>A network adapter present in the machine.</summary>
public sealed record NetworkAdapter
{
    /// <summary>Adapter GUID as used in the network registry hive.</summary>
    public required string InterfaceId { get; init; }

    /// <summary>Connection name shown in Windows, for example <c>Ethernet</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Driver description of the adapter hardware.</summary>
    public required string Description { get; init; }

    /// <summary>Physical medium.</summary>
    public required NetworkInterfaceKind Kind { get; init; }

    /// <summary>Negotiated link speed in bits per second, or <c>0</c> when down.</summary>
    public long LinkSpeedBitsPerSecond { get; init; }

    /// <summary><see langword="true"/> when the interface is operationally up.</summary>
    public bool IsUp { get; init; }

    /// <summary><see langword="true"/> when the default IPv4 route uses this adapter.</summary>
    public bool CarriesDefaultRoute { get; init; }

    /// <summary>Hardware address. Treated as personal data and redacted by the log pipeline.</summary>
    public string? PhysicalAddress { get; init; }

    /// <summary>Configured DNS server addresses.</summary>
    public IReadOnlyList<string> DnsServers { get; init; } = new List<string>();

    /// <summary>Driver exposed capabilities.</summary>
    public NetworkAdapterCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Registry key holding this adapter's driver keywords, for example
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\0012</c>.
    /// </summary>
    /// <remarks>
    /// Carrying the location on the model is what lets a portable module address a keyword through
    /// the ordinary registry provider, rather than needing a network specific state provider that
    /// would have to re-enumerate the class key on every read.
    /// </remarks>
    public string? DriverRegistryPath { get; init; }
}
