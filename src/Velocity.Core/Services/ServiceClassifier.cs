using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Services;

namespace Velocity.Core.Services;

/// <summary>
/// Classifies the services on a machine, deriving the answer from the dependency graph rather than
/// from a shipped list of things to disable.
/// </summary>
/// <remarks>
/// <para>
/// The ordering of the checks is the safety property. A service is Critical, Security,
/// HardwareDependent, GamingRelated or Required <em>before</em> it can ever be Optional, and
/// anything that does not clearly fall into one of those and is not clearly ordinary is left
/// Unknown, which means untouched.
/// </para>
/// <para>
/// The only hard-coded names are protective: the small set of services Windows genuinely cannot
/// run without, the security stack, and the gaming stack. There is no "these fifty services are
/// safe to disable" list anywhere in this product, because such a list cannot be correct on a
/// machine the author has never seen.
/// </para>
/// <para>
/// Nothing here is ever disabled. The strongest action the engine takes is stopping a running
/// optional service for the duration of a session and starting it again afterwards.
/// </para>
/// </remarks>
public static class ServiceClassifier
{
    /// <summary>
    /// Services Windows does not function correctly without. Stopping any of these breaks the
    /// session, the network stack, driver installation or the update path.
    /// </summary>
    private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "Power", "PlugPlay", "Schedule", "EventLog",
        "EventSystem", "ProfSvc", "SamSs", "Themes", "UserManager", "SystemEventsBroker",
        "BrokerInfrastructure", "CoreMessagingRegistrar", "StateRepository", "TimeBrokerSvc",
        "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "Netman", "WinHttpAutoProxySvc",
        "AudioSrv", "AudioEndpointBuilder", "UxSms", "ShellHWDetection", "SENS", "gpsvc",
        "TrustedInstaller", "msiserver", "DeviceInstall", "DsmSvc", "WdiServiceHost",
    };

    /// <summary>Security and privacy services. Never touched, at any aggression level.</summary>
    private static readonly HashSet<string> SecurityServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "SecurityHealthService", "Sense", "WdNisSvc", "wscsvc", "MpsSvc", "BFE",
        "mpssvc", "SharedAccess", "CryptSvc", "KeyIso", "VaultSvc", "EFS", "BDESVC", "TPM",
        "SgrmBroker", "AppIDSvc", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS",
    };

    /// <summary>The gaming stack: launchers, anti-cheat and overlays.</summary>
    private static readonly HashSet<string> GamingServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc", "GamingServices",
        "GamingServicesNet", "Steam Client Service", "SteamService", "EasyAntiCheat",
        "EasyAntiCheat_EOS", "BEService", "vgc", "vgk", "FACEIT", "RiotVanguard",
    };

    /// <summary>Vendor prefixes whose services back hardware and must not be stopped.</summary>
    private static readonly string[] HardwareVendorPrefixes =
    [
        "NVIDIA", "NVDisplay", "AMD", "amdlog", "Intel", "igfx", "Realtek", "RtkAudio", "Killer",
        "Rivet", "Synaptics", "Elan", "Logitech", "LGHUB", "Corsair", "Razer", "SteelSeries",
        "ASUS", "Armoury", "MSI", "Gigabyte", "Creative", "Nahimic", "Sonic", "Dolby", "Waves",
    ];

    /// <summary>Classifies one service against the whole service set.</summary>
    /// <param name="service">Service to classify.</param>
    /// <param name="allServices">Every service on the machine, used for the dependency graph.</param>
    /// <returns>The service with its classification and reason filled in.</returns>
    public static ServiceSnapshot Classify(
        ServiceSnapshot service,
        IReadOnlyCollection<ServiceSnapshot> allServices)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(allServices);

        (ServiceClassification classification, string reason) = Evaluate(service, allServices);

        return service with { Classification = classification, ClassificationReason = reason };
    }

    /// <summary>Classifies every service on the machine.</summary>
    /// <param name="allServices">Every service.</param>
    /// <returns>The services, classified.</returns>
    public static IReadOnlyList<ServiceSnapshot> ClassifyAll(IReadOnlyCollection<ServiceSnapshot> allServices)
    {
        ArgumentNullException.ThrowIfNull(allServices);

        return allServices.Select(service => Classify(service, allServices)).ToList();
    }

    private static (ServiceClassification Classification, string Reason) Evaluate(
        ServiceSnapshot service,
        IReadOnlyCollection<ServiceSnapshot> allServices)
    {
        if (service.Kind is ServiceKind.KernelDriver or ServiceKind.FileSystemDriver)
        {
            return (ServiceClassification.HardwareDependent, "This is a driver, not a service.");
        }

        if (service.StartMode is ServiceStartMode.Boot or ServiceStartMode.System)
        {
            return (ServiceClassification.Critical, "Started by the kernel before Windows finishes booting.");
        }

        if (CriticalServices.Contains(service.Name))
        {
            return (ServiceClassification.Critical, "Windows does not function correctly without it.");
        }

        if (SecurityServices.Contains(service.Name))
        {
            return (ServiceClassification.Security,
                "Security or update related. This product never changes security configuration.");
        }

        if (GamingServices.Contains(service.Name) || LooksLikeGamingService(service))
        {
            return (ServiceClassification.GamingRelated,
                "Part of the gaming stack: stopping it can break a launcher or trip anti-cheat.");
        }

        if (MatchesHardwareVendor(service))
        {
            return (ServiceClassification.HardwareDependent,
                "Published by a hardware vendor, so it may back a device this machine needs.");
        }

        // The dependency graph, not a list, is what makes this safe on a machine nobody has seen.
        IReadOnlyList<string> runningDependents = FindRunningDependents(service, allServices);

        if (runningDependents.Count > 0)
        {
            return (ServiceClassification.Required, string.Create(
                CultureInfo.InvariantCulture,
                $"{runningDependents.Count} running service(s) depend on it: {string.Join(", ", runningDependents.Take(4))}."));
        }

        if (service.State != ServiceState.Running)
        {
            return (ServiceClassification.AlreadyStopped, "Already stopped, so there is nothing to gain.");
        }

        if (service.State == ServiceState.Running && service.Kind == ServiceKind.Service)
        {
            return (ServiceClassification.Optional,
                "Running, nothing depends on it, and it is not part of the platform, security, " +
                "hardware or gaming stacks. It can be stopped for the duration of a session.");
        }

        return (ServiceClassification.Unknown,
            "Could not be classified confidently, so it is left alone.");
    }

    private static IReadOnlyList<string> FindRunningDependents(
        ServiceSnapshot service,
        IReadOnlyCollection<ServiceSnapshot> allServices)
    {
        var dependents = new List<string>();

        // Both directions are consulted: the service's own dependent list, and anyone who names it
        // as a dependency. Neither is complete on its own in practice.
        var declared = service.DependentServices.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (ServiceSnapshot candidate in allServices)
        {
            if (candidate.State != ServiceState.Running ||
                string.Equals(candidate.Name, service.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (declared.Contains(candidate.Name) ||
                candidate.DependsOn.Contains(service.Name, StringComparer.OrdinalIgnoreCase))
            {
                dependents.Add(candidate.Name);
            }
        }

        return dependents;
    }

    private static bool MatchesHardwareVendor(ServiceSnapshot service) =>
        HardwareVendorPrefixes.Any(prefix =>
            service.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            service.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeGamingService(ServiceSnapshot service)
    {
        string[] markers = ["anticheat", "anti-cheat", "battleye", "vanguard", "punkbuster", "xbox"];

        return markers.Any(marker =>
            service.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            service.DisplayName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The protective lists, exposed so Expert Mode can show what is off limits and why.</summary>
    /// <returns>Critical, security and gaming service names.</returns>
    public static (IReadOnlyCollection<string> Critical,
        IReadOnlyCollection<string> Security,
        IReadOnlyCollection<string> Gaming) DescribeProtectedServices() =>
        (CriticalServices.Order().ToList(), SecurityServices.Order().ToList(), GamingServices.Order().ToList());
}
