using System;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Telemetry;
using Velocity.Platform.Windows.Ipc;
using Velocity.Abstractions.Processes;
using Velocity.Abstractions.Services;
using Velocity.Core.Processes;
using Velocity.Core.Services;
using Velocity.Platform.Windows.Privileges;
using Velocity.Platform.Windows.Processes;
using Velocity.Platform.Windows.Services;
using Velocity.Platform.Windows.Probes;
using Velocity.Platform.Windows.Telemetry;
using Velocity.Platform.Windows.State;

namespace Velocity.Platform.Windows;

/// <summary>Registers the Windows implementations of the platform contracts.</summary>
public static class WindowsPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Adds the hardware probes, the registry state provider, the privilege context and the client
    /// side of the privileged channel.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <param name="configurePipe">Optional named pipe configuration.</param>
    /// <returns>The same service collection.</returns>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddVelocityWindowsPlatform(
        this IServiceCollection services,
        Action<NamedPipeChannelOptions>? configurePipe = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var pipeOptions = new NamedPipeChannelOptions();
        configurePipe?.Invoke(pipeOptions);
        services.TryAddSingleton(pipeOptions);

        services.TryAddSingleton<IPrivilegedChannel, NamedPipePrivilegedChannel>();

        services.TryAddSingleton<IPrivilegeContext>(provider =>
        {
            // The availability probe is a callback rather than a snapshot because the helper
            // service can be started or stopped while the application is running.
            IPrivilegedChannel channel = provider.GetRequiredService<IPrivilegedChannel>();
            return new WindowsPrivilegeContext(() => channel.IsAvailable);
        });

        services.TryAddSingleton<ICpuTopologyProbe, WindowsCpuTopologyProbe>();
        services.TryAddSingleton<IOperatingSystemProbe, WindowsOperatingSystemProbe>();
        services.TryAddSingleton<IMemoryProbe, WindowsMemoryProbe>();
        services.TryAddSingleton<IGpuProbe, WindowsGpuProbe>();
        services.TryAddSingleton<IStorageProbe, WindowsStorageProbe>();
        services.TryAddSingleton<INetworkProbe, WindowsNetworkProbe>();
        services.TryAddSingleton<IDisplayProbe, WindowsDisplayProbe>();
        services.TryAddSingleton<IPowerProbe, WindowsPowerProbe>();
        services.TryAddSingleton<IPlatformSecurityProbe, WindowsPlatformSecurityProbe>();
        services.TryAddSingleton<IMachineKindProbe, WindowsMachineKindProbe>();

        services.AddSingleton<IStateProvider>(provider => new RegistryStateProvider(
            provider.GetRequiredService<IPrivilegeContext>(),
            provider.GetRequiredService<ILogger<RegistryStateProvider>>(),
            provider.GetRequiredService<IPrivilegedChannel>()));

        services.AddSingleton<ITelemetryProvider>(provider => new PerformanceCounterTelemetryProvider(
            provider.GetRequiredService<ILogger<PerformanceCounterTelemetryProvider>>(),
            provider.GetRequiredService<ISystemProfileProvider>()));

        services.TryAddSingleton<IProcessInspector, WindowsProcessInspector>();
        services.TryAddSingleton<IProcessController, WindowsProcessController>();

        services.AddSingleton<IStateProvider>(provider => new ProcessStateProvider(
            provider.GetRequiredService<IProcessInspector>(),
            provider.GetRequiredService<IProcessController>(),
            provider.GetRequiredService<ILogger<ProcessStateProvider>>()));

        services.TryAddSingleton<IServiceInspector, WindowsServiceInspector>();
        services.TryAddSingleton<Abstractions.Services.IServiceController, WindowsServiceController>();

        services.AddSingleton<IStateProvider>(provider => new ServiceStateProvider(
            provider.GetRequiredService<IServiceInspector>(),
            provider.GetRequiredService<Abstractions.Services.IServiceController>(),
            provider.GetRequiredService<ILogger<ServiceStateProvider>>()));

        services.AddSingleton<ITelemetryProvider>(provider => new GpuTelemetryProvider(
            provider.GetRequiredService<ILogger<GpuTelemetryProvider>>(),
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Adds the platform services needed inside the privileged helper: the same registry provider,
    /// but performing writes directly instead of forwarding them to itself.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same service collection.</returns>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddVelocityWindowsHelperPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPrivilegeContext>(_ => new WindowsPrivilegeContext(() => false));
        services.AddSingleton<IStateProvider>(provider => new RegistryStateProvider(
            provider.GetRequiredService<IPrivilegeContext>(),
            provider.GetRequiredService<ILogger<RegistryStateProvider>>(),
            channel: null));

        return services;
    }
}
