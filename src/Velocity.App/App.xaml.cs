using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Velocity.Composition;
using Velocity.Core.Transactions;
using Velocity.Presentation;
using Velocity.Presentation.Mvvm;
using Velocity.Presentation.Navigation;

namespace Velocity.App;

/// <summary>
/// Application entry point.
/// </summary>
/// <remarks>
/// The startup order is not incidental. The database is migrated and any transaction left in
/// flight by a crash is rolled back <em>before</em> the window is shown, because the user must
/// never see a dashboard describing a machine that is still half-configured by a previous run.
/// </remarks>
public partial class App : Application
{
    private ServiceProvider? _services;
    private Window? _window;

    /// <summary>Creates the application.</summary>
    public App() => InitializeComponent();

    /// <summary>Services available to the views.</summary>
    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The application has not finished starting.");

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = new WinUiDispatcher();

        _services = VelocityHost.Build(
            writeToConsole: false,
            configureServices: registrations => registrations.AddSingleton<IUiDispatcher>(dispatcher));

        UnhandledException += OnUnhandledException;

        _window = new MainWindow(_services);
        dispatcher.Attach(_window.DispatcherQueue);

        _window.Activate();

        // Recovery is started after activation so the window appears promptly, but the shell keeps
        // its busy state until it completes and no page can open a transaction before then.
        _ = RunStartupAsync(_services);
    }

    private static async Task RunStartupAsync(IServiceProvider services)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Velocity.App");

        try
        {
            RecoveryReport report = await VelocityHost.StartAsync(services, CancellationToken.None)
                .ConfigureAwait(false);

            if (!report.NothingToDo)
            {
                logger.LogWarning(
                    "Startup restored {Count} transaction(s) left behind by a previous run.",
                    report.RecoveredTransactionCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Startup failed.");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        ILogger logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("Velocity.App");
        logger.LogCritical(e.Exception, "Unhandled exception on the UI thread.");
    }
}
