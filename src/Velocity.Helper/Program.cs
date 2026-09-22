using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Velocity.Diagnostics;
using Velocity.Diagnostics.Logging;
using Velocity.Helper.Handlers;
using Velocity.Ipc;
using Velocity.Platform.Windows;
using Velocity.Platform.Windows.Ipc;

namespace Velocity.Helper;

/// <summary>
/// Entry point of the privileged helper.
/// </summary>
/// <remarks>
/// <para>
/// The helper exists so the desktop application never has to run elevated. It is deliberately
/// small: it exposes a short, fixed list of operations, applies a policy to each one, and has no
/// user interface, no network access and no plug-in surface. Everything that can be decided
/// without elevation is decided in the desktop process.
/// </para>
/// <para>
/// It runs as a Windows service in an installed deployment and as a console application during
/// development, using the same code path in both cases.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>Runs the helper.</summary>
    /// <param name="args">Command line arguments passed by the service control manager.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "VelocityHelper");

        var paths = new VelocityPaths();
        builder.Services.AddVelocityDiagnostics(paths, options =>
        {
            options.FileNamePrefix = "velocity-helper";
            options.WriteToConsole = !WindowsServiceHelpers.IsWindowsService();
        });

        builder.Services.AddVelocityWindowsHelperPlatform();

        builder.Services.AddSingleton(new NamedPipeChannelOptions());
        builder.Services.AddSingleton<IIpcRequestHandler, PingHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, GetIdentityHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, RegistryReadHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, RegistryWriteHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, PowerGetActiveSchemeHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, PowerSetActiveSchemeHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, PowerReadAcValueHandler>();
        builder.Services.AddSingleton<IIpcRequestHandler, PowerWriteAcValueHandler>();

        builder.Services.AddSingleton(provider => new IpcServer(
            provider.GetServices<IIpcRequestHandler>(),
            provider.GetRequiredService<ILogger<IpcServer>>()));

        builder.Services.AddSingleton<NamedPipeServerHost>();
        builder.Services.AddHostedService<HelperService>();

        using IHost host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
