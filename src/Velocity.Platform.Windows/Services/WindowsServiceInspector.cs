using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Services;

// Both namespaces declare these names; the product's own model is the one meant throughout.
using ServiceKind = Velocity.Abstractions.Services.ServiceKind;
using ServiceStartMode = Velocity.Abstractions.Services.ServiceStartMode;
using ServiceState = Velocity.Abstractions.Services.ServiceState;
using Velocity.Platform.Windows.Interop;

namespace Velocity.Platform.Windows.Services;

/// <summary>Reads the service control manager.</summary>
/// <remarks>
/// <para>
/// Start mode, account, image path and description come from <c>Win32_Service</c>; live state and
/// the dependency graph come from <see cref="ServiceController"/>. Both are needed: WMI does not
/// expose the dependency lists usefully, and <c>ServiceController</c> does not expose the start
/// mode on every Windows edition.
/// </para>
/// <para>
/// The two sources are joined on the service name, and a service present in one but not the other
/// still appears with whatever is known, because a service missing from the list is also missing
/// from the classifier's dependency graph.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceInspector : IServiceInspector
{
    private readonly ILogger<WindowsServiceInspector> _logger;

    /// <summary>Creates the inspector.</summary>
    /// <param name="logger">Logger.</param>
    public WindowsServiceInspector(ILogger<WindowsServiceInspector> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceSnapshot>> GetServicesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, WmiServiceRecord> configuration = ReadConfiguration();
        var snapshots = new List<ServiceSnapshot>();

        foreach (ServiceController controller in ServiceController.GetServices())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                snapshots.Add(Describe(controller, configuration));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                _logger.LogTrace(ex, "Service {Service} could not be read.", controller.ServiceName);
            }
            finally
            {
                controller.Dispose();
            }
        }

        return Task.FromResult<IReadOnlyList<ServiceSnapshot>>(snapshots);
    }

    /// <inheritdoc />
    public Task<ServiceSnapshot?> GetServiceAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var controller = new ServiceController(name);
            return Task.FromResult<ServiceSnapshot?>(Describe(controller, ReadConfiguration()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
        {
            _logger.LogTrace(ex, "Service {Service} does not exist.", name);
            return Task.FromResult<ServiceSnapshot?>(null);
        }
    }

    private ServiceSnapshot Describe(
        ServiceController controller,
        IReadOnlyDictionary<string, WmiServiceRecord> configuration)
    {
        configuration.TryGetValue(controller.ServiceName, out WmiServiceRecord? record);

        return new ServiceSnapshot
        {
            Name = controller.ServiceName,
            DisplayName = controller.DisplayName,
            State = MapState(controller),
            StartMode = MapStartMode(record?.StartMode),
            Kind = MapKind(controller.ServiceType),
            DependsOn = ReadNames(() => controller.ServicesDependedOn),
            DependentServices = ReadNames(() => controller.DependentServices),
            Account = record?.Account,
            Description = record?.Description,
            ImagePath = record?.PathName,
            IsMicrosoftService = record?.PathName is string path &&
                                 path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase),
        };
    }

    private Dictionary<string, WmiServiceRecord> ReadConfiguration()
    {
        var configuration = new Dictionary<string, WmiServiceRecord>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (WmiRecord row in WmiQuery.Run(
                "SELECT Name, StartMode, StartName, PathName, Description FROM Win32_Service"))
            {
                string? name = row.GetString("Name");
                if (name is null)
                {
                    continue;
                }

                configuration[name] = new WmiServiceRecord(
                    row.GetString("StartMode"),
                    row.GetString("StartName"),
                    row.GetString("PathName"),
                    row.GetString("Description"));
            }
        }
        catch (Exception ex)
        {
            // Without WMI the start mode and account are unknown, which degrades the classifier to
            // its dependency-graph signals rather than breaking it.
            _logger.LogWarning(ex, "Service configuration could not be read from WMI.");
        }

        return configuration;
    }

    private string[] ReadNames(Func<ServiceController[]> read)
    {
        try
        {
            ServiceController[] services = read();
            try
            {
                return services.Select(service => service.ServiceName).ToArray();
            }
            finally
            {
                foreach (ServiceController service in services)
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _logger.LogTrace(ex, "A service dependency list could not be read.");
            return [];
        }
    }

    private static ServiceState MapState(ServiceController controller)
    {
        try
        {
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.Paused => ServiceState.Paused,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending or
                    ServiceControllerStatus.ContinuePending or ServiceControllerStatus.PausePending =>
                    ServiceState.Transitioning,
                _ => ServiceState.Unknown,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return ServiceState.Unknown;
        }
    }

    private static ServiceStartMode MapStartMode(string? startMode) => startMode?.ToUpperInvariant() switch
    {
        "BOOT" => ServiceStartMode.Boot,
        "SYSTEM" => ServiceStartMode.System,
        "AUTO" => ServiceStartMode.Automatic,
        "MANUAL" => ServiceStartMode.Manual,
        "DISABLED" => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Unknown,
    };

    private static ServiceKind MapKind(ServiceType type) => type switch
    {
        ServiceType.KernelDriver => ServiceKind.KernelDriver,
        ServiceType.FileSystemDriver => ServiceKind.FileSystemDriver,
        _ => ServiceKind.Service,
    };

    private sealed record WmiServiceRecord(
        string? StartMode,
        string? Account,
        string? PathName,
        string? Description);
}
