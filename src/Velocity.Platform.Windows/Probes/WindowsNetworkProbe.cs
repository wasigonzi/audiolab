using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velocity.Abstractions.Hardware;

namespace Velocity.Platform.Windows.Probes;

/// <summary>
/// Enumerates network adapters and reads the advanced keywords their drivers publish.
/// </summary>
/// <remarks>
/// <para>
/// The keywords under the network class key are the only honest source for what an adapter can
/// actually do. A driver that does not publish <c>*InterruptModeration</c> does not support the
/// setting, and writing the value anyway would change nothing while appearing to succeed, which is
/// exactly the behaviour this product exists not to have.
/// </para>
/// <para>
/// Every capability is therefore three valued: enabled, disabled, or not exposed by this driver.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkProbe : INetworkProbe
{
    private const string NetworkClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private readonly ILogger<WindowsNetworkProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsNetworkProbe(ILogger<WindowsNetworkProbe> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProbeName => "network";

    /// <inheritdoc />
    public Task<IReadOnlyList<NetworkAdapter>> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, Dictionary<string, string>> keywordsByInterfaceId = ReadDriverKeywords();
        var adapters = new List<NetworkAdapter>();

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            keywordsByInterfaceId.TryGetValue(adapter.Id, out Dictionary<string, string>? keywords);

            adapters.Add(new NetworkAdapter
            {
                InterfaceId = adapter.Id,
                Name = adapter.Name,
                Description = adapter.Description,
                Kind = MapKind(adapter.NetworkInterfaceType),
                LinkSpeedBitsPerSecond = adapter.OperationalStatus == OperationalStatus.Up ? adapter.Speed : 0,
                IsUp = adapter.OperationalStatus == OperationalStatus.Up,
                CarriesDefaultRoute = properties.GatewayAddresses.Count > 0,
                PhysicalAddress = FormatMac(adapter.GetPhysicalAddress()),
                DnsServers = properties.DnsAddresses.Select(address => address.ToString()).ToList(),
                Capabilities = BuildCapabilities(keywords),
                DriverRegistryPath = pathByInterfaceId.TryGetValue(adapter.Id, out string? driverPath)
                    ? $@"HKLM\{driverPath}"
                    : null,
            });
        }

        return Task.FromResult<IReadOnlyList<NetworkAdapter>>(adapters);
    }

    private static NetworkAdapterCapabilities BuildCapabilities(Dictionary<string, string>? keywords)
    {
        if (keywords is null)
        {
            return new NetworkAdapterCapabilities();
        }

        return new NetworkAdapterCapabilities
        {
            ReceiveSideScalingEnabled = ReadFlag(keywords, "*RSS"),
            ReceiveSideScalingQueues = ReadInteger(keywords, "*NumRssQueues"),
            InterruptModerationSupported = keywords.ContainsKey("*InterruptModeration"),
            InterruptModerationEnabled = ReadFlag(keywords, "*InterruptModeration"),
            EnergyEfficientEthernetEnabled = ReadFlag(keywords, "*EEE"),

            PowerManagementEnabled = ReadPowerManagement(keywords),
            AdvancedProperties = keywords,
        };
    }

    /// <summary>
    /// Interprets <c>PnPCapabilities</c> conservatively.
    /// </summary>
    /// <remarks>
    /// Absent means the driver uses the Windows default, which permits power management. Zero means
    /// the same thing explicitly. 24 is the documented value for clearing both power management
    /// checkboxes. Every other value is left unknown rather than decoded from bit positions this
    /// product is not certain of, because reporting a guess as a fact is exactly what it must not do.
    /// </remarks>
    private static bool? ReadPowerManagement(Dictionary<string, string> keywords)
    {
        if (!keywords.TryGetValue("PnPCapabilities", out string? raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return true;
        }

        return value switch
        {
            0 => true,
            24 => false,
            _ => null,
        };
    }

    private static bool? ReadFlag(Dictionary<string, string> keywords, string name) =>
        keywords.TryGetValue(name, out string? value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed != 0
            : null;

    private static int? ReadInteger(Dictionary<string, string> keywords, string name) =>
        keywords.TryGetValue(name, out string? value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    private readonly Dictionary<string, string> pathByInterfaceId = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Dictionary<string, string>> ReadDriverKeywords()
    {
        var byInterfaceId = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        pathByInterfaceId.Clear();

        try
        {
            using RegistryKey? networkClass = Registry.LocalMachine.OpenSubKey(NetworkClassPath);
            if (networkClass is null)
            {
                return byInterfaceId;
            }

            foreach (string subKeyName in networkClass.GetSubKeyNames())
            {
                if (!subKeyName.All(char.IsDigit))
                {
                    continue;
                }

                using RegistryKey? adapterKey = networkClass.OpenSubKey(subKeyName);
                if (adapterKey?.GetValue("NetCfgInstanceId") is not string interfaceId)
                {
                    continue;
                }

                var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string valueName in adapterKey.GetValueNames())
                {
                    // Driver keywords start with '*'; PnPCapabilities is the documented exception
                    // worth carrying because it controls adapter power down.
                    if (!valueName.StartsWith('*') &&
                        !string.Equals(valueName, "PnPCapabilities", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    object? value = adapterKey.GetValue(valueName);
                    if (value is not null)
                    {
                        keywords[valueName] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }

                byInterfaceId[interfaceId] = keywords;
                pathByInterfaceId[interfaceId] = $@"{NetworkClassPath}\{subKeyName}";
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read network adapter driver keywords.");
        }

        return byInterfaceId;
    }

    private static NetworkInterfaceKind MapKind(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx =>
            NetworkInterfaceKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetworkInterfaceKind.Wireless,
        NetworkInterfaceType.Loopback => NetworkInterfaceKind.Loopback,
        NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => NetworkInterfaceKind.Virtual,
        _ => NetworkInterfaceKind.Unknown,
    };

    private static string? FormatMac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join('-', bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
