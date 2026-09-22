using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Transactions;
using Velocity.Abstractions.Tweaks;
using Velocity.Core.Hardware;
using Velocity.Core.Transactions;
using Velocity.Core.Tweaks;
using Velocity.Data.Repositories;
using Velocity.Diagnostics.Logging;
using Velocity.Ipc.Authorization;

namespace Velocity.Cli;

/// <summary>
/// Command line host for the optimization engine.
/// </summary>
/// <remarks>
/// This exists for three reasons: support staff can capture a machine's state without installing
/// the desktop application, the engine can be driven on real hardware before the interface exists,
/// and every phase of the product has a way to be exercised end to end that does not depend on a
/// window being open.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>Runs a command.</summary>
    /// <param name="args">Command and its arguments.</param>
    /// <returns>Zero on success, one on a handled failure, two on a usage error.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        // The policy listing needs no database, no elevation and no hardware, so it is answered
        // before the host is built.
        if (string.Equals(args[0], "policy", StringComparison.OrdinalIgnoreCase))
        {
            PrintPolicy();
            return 0;
        }

        await using ServiceProvider services = VelocityHost.Build(writeToConsole: false);

        try
        {
            await VelocityHost.StartAsync(services, cancellation.Token).ConfigureAwait(false);

            return args[0].ToLowerInvariant() switch
            {
                "info" => await RunInfoAsync(services, cancellation.Token).ConfigureAwait(false),
                "catalogue" or "catalog" =>
                    await RunCatalogueAsync(services, cancellation.Token).ConfigureAwait(false),
                "detect" => await RunDetectAsync(services, cancellation.Token).ConfigureAwait(false),
                "history" => await RunHistoryAsync(services, cancellation.Token).ConfigureAwait(false),
                "recover" => await RunRecoverAsync(services, cancellation.Token).ConfigureAwait(false),
                "rollback" => await RunRollbackAsync(services, args, cancellation.Token).ConfigureAwait(false),
                "logs" => await RunExportLogsAsync(services, cancellation.Token).ConfigureAwait(false),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunInfoAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var provider = services.GetRequiredService<ISystemProfileProvider>();
        SystemProfile profile = await provider.RefreshAsync(cancellationToken).ConfigureAwait(false);
        CpuLayout layout = CpuTopologyAnalyzer.Analyze(profile.Cpu);

        Console.WriteLine($"Fingerprint       {profile.Fingerprint.ShortId}");
        Console.WriteLine($"Operating system  {profile.OperatingSystem.ProductName} " +
                          $"{profile.OperatingSystem.DisplayVersion} " +
                          $"(build {profile.OperatingSystem.BuildNumber}.{profile.OperatingSystem.UpdateBuildRevision})");
        Console.WriteLine($"Machine           {profile.MachineKind}");
        Console.WriteLine($"Processor         {profile.Cpu.BrandString}");
        Console.WriteLine($"                  {profile.Cpu.PhysicalCoreCount} cores / " +
                          $"{profile.Cpu.LogicalProcessorCount} threads, " +
                          $"{profile.Cpu.Groups.Count} group(s), {profile.Cpu.NumaNodes.Count} NUMA node(s)");
        Console.WriteLine($"Memory            {FormatBytes(profile.Memory.TotalPhysicalBytes)} installed, " +
                          $"{FormatBytes(profile.Memory.AvailablePhysicalBytes)} available");

        foreach (GpuDevice gpu in profile.Gpus)
        {
            Console.WriteLine($"Graphics          {gpu.Description} (driver {gpu.DriverVersion ?? "unknown"})");
        }

        foreach (DisplayDevice display in profile.Displays)
        {
            string warning = display.IsRunningBelowMaximumRefreshRate
                ? $"  <-- supports {display.MaximumRefreshRateHzAtCurrentResolution} Hz"
                : string.Empty;

            Console.WriteLine($"Display           {display.FriendlyName ?? display.DeviceName}: " +
                              $"{display.CurrentMode.Width}x{display.CurrentMode.Height} " +
                              $"@ {display.CurrentMode.RefreshRateHz} Hz{warning}");
        }

        foreach (StorageDevice device in profile.StorageDevices)
        {
            Console.WriteLine($"Storage           {device.Model} " +
                              $"({device.MediaType}, {device.BusType}, {FormatBytes(device.SizeBytes)})");
        }

        Console.WriteLine($"Power plan        {profile.Power.ActiveScheme.Name}");

        if (layout.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Topology analysis:");
            foreach (string note in layout.Notes)
            {
                Console.WriteLine($"  - {note}");
            }
        }

        if (profile.ProbeFailures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Probes that failed:");
            foreach (KeyValuePair<string, string> failure in profile.ProbeFailures)
            {
                Console.WriteLine($"  - {failure.Key}: {failure.Value}");
            }
        }

        return 0;
    }

    private static async Task<int> RunCatalogueAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<ITweakRegistry>();
        var contextFactory = services.GetRequiredService<ITweakContextFactory>();

        if (registry.All.Count == 0)
        {
            Console.WriteLine("This build contains no optimization modules yet.");
            Console.WriteLine("The engine, journal and rollback pipeline are in place; modules arrive in Phase 3.");
            return 0;
        }

        (TweakContext context, _) = await contextFactory
            .CreateForKeysAsync("cli", Array.Empty<Abstractions.State.StateKey>(), cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CatalogueEntry> entries =
            await registry.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);

        foreach (CatalogueEntry entry in entries)
        {
            TweakDescriptor descriptor = entry.Tweak.Descriptor;
            string status = entry.Compatibility.IsSupported ? "supported" : entry.Compatibility.Status.ToString();
            Console.WriteLine($"{descriptor.Id,-44} {descriptor.Category,-12} {descriptor.Risk,-12} {status}");

            if (!entry.Compatibility.IsSupported)
            {
                Console.WriteLine($"{string.Empty,-44} {entry.Compatibility.Reason}");
            }
        }

        return 0;
    }

    private static async Task<int> RunDetectAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var engine = services.GetRequiredService<IOptimizationEngine>();
        IReadOnlyList<TweakRunResult> results = await engine.DetectAsync(cancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
        {
            Console.WriteLine("This build contains no optimization modules yet.");
            return 0;
        }

        foreach (TweakRunResult result in results)
        {
            Console.WriteLine($"{result.TweakId,-44} {result.Observation?.State.ToString() ?? "n/a",-18} {result.Message}");
        }

        return 0;
    }

    private static async Task<int> RunHistoryAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var journal = services.GetRequiredService<ITransactionJournal>();
        IReadOnlyList<OptimizationTransaction> transactions =
            await journal.GetRecentTransactionsAsync(25, cancellationToken).ConfigureAwait(false);

        if (transactions.Count == 0)
        {
            Console.WriteLine("This product has not changed anything on this machine.");
            return 0;
        }

        foreach (OptimizationTransaction transaction in transactions)
        {
            Console.WriteLine(
                $"{transaction.StartedAtUtc:u}  {transaction.Id}  {transaction.Status,-20} {transaction.Reason}");

            foreach (TransactionStep step in transaction.Steps)
            {
                string rolledBack = step.RolledBack ? " (rolled back)" : string.Empty;
                Console.WriteLine($"    {step.Ordinal}. {step.TweakId} -> {step.ApplyOutcome}{rolledBack}");
            }
        }

        return 0;
    }

    private static async Task<int> RunRecoverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var recovery = services.GetRequiredService<ICrashRecoveryService>();
        RecoveryReport report = await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine(report.NothingToDo
            ? "Nothing needed recovering."
            : $"Recovered {report.RecoveredTransactionCount} transaction(s); " +
              $"{report.FailedTransactionIds.Count} could not be restored.");

        return report.FailedTransactionIds.Count == 0 ? 0 : 1;
    }

    private static async Task<int> RunRollbackAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var rollback = services.GetRequiredService<IRollbackEngine>();
        RollbackResult? result;

        if (args.Length >= 3 && string.Equals(args[1], "--transaction", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(args[2], out Guid transactionId))
            {
                Console.Error.WriteLine($"'{args[2]}' is not a transaction id.");
                return 2;
            }

            result = await rollback.RollbackTransactionAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (args.Length >= 3 && string.Equals(args[1], "--tweak", StringComparison.OrdinalIgnoreCase))
        {
            result = await rollback.RollbackTweakAsync(args[2], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await rollback.RollbackLastAsync(cancellationToken).ConfigureAwait(false);
        }

        if (result is null)
        {
            Console.WriteLine("There is nothing applied by this product to roll back.");
            return 0;
        }

        Console.WriteLine($"Restored {result.RestoredKeyCount} value(s) from transaction {result.TransactionId}.");

        foreach (KeyValuePair<string, string> failure in result.Failures)
        {
            Console.Error.WriteLine($"  failed: {failure.Key}: {failure.Value}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> RunExportLogsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var exporter = services.GetRequiredService<ISupportBundleExporter>();
        string path = await exporter
            .ExportAsync(Environment.CurrentDirectory, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"Support bundle written to {path}");
        return 0;
    }

    private static void PrintPolicy()
    {
        Console.WriteLine("Registry paths the privileged helper may write:");
        foreach ((string prefix, string purpose) in PrivilegedOperationPolicy.DescribeWriteAllowList())
        {
            Console.WriteLine($"  {prefix}");
            Console.WriteLine($"      {purpose}");
        }

        Console.WriteLine();
        Console.WriteLine("Registry paths that are never written, whatever a module asks for:");
        foreach ((string prefix, string reason) in PrivilegedOperationPolicy.DescribeDenyList())
        {
            Console.WriteLine($"  {prefix}");
            Console.WriteLine($"      {reason}");
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static bool IsHelp(string argument) =>
        argument is "-h" or "--help" or "help" or "/?";

    private static void PrintUsage()
    {
        Console.WriteLine("velocity <command>");
        Console.WriteLine();
        Console.WriteLine("  info                          Describe this machine and what the analyzer makes of it.");
        Console.WriteLine("  catalogue                     List optimization modules and their compatibility.");
        Console.WriteLine("  detect                        Report what each module observes, changing nothing.");
        Console.WriteLine("  history                       Show what this product has changed on this machine.");
        Console.WriteLine("  recover                       Roll back any transaction left in flight by a crash.");
        Console.WriteLine("  rollback [--transaction <id> | --tweak <id>]");
        Console.WriteLine("                                Restore captured values. Defaults to the last transaction.");
        Console.WriteLine("  policy                        Print the privileged write allow and deny lists.");
        Console.WriteLine("  logs                          Write a redacted support bundle to the current directory.");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}
