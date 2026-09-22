using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Velocity.Presentation.Mvvm;

/// <summary>
/// Common behaviour for every view model: busy state, error surfacing and cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> exists so that no view model has to write its own try/catch/finally
/// around an async command. An unhandled exception in a command would otherwise either crash the
/// process or, worse, leave the UI stuck in a busy state with no explanation.
/// </para>
/// <para>
/// Each view model owns a cancellation token source that is cancelled when the view is navigated
/// away from, so background loads do not keep running behind a page nobody is looking at. That
/// matters here more than in a typical application: the optimizer must be close to idle while a
/// game is running.
/// </para>
/// </remarks>
public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    /// <summary>Creates the view model.</summary>
    /// <param name="logger">Logger for this view model.</param>
    protected ViewModelBase(ILogger logger) =>
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Logger for this view model.</summary>
    protected ILogger Logger { get; }

    /// <summary>Token cancelled when the view model is disposed.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <summary>Whether a long running operation is in progress.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Message describing the operation in progress, shown next to the busy indicator.</summary>
    [ObservableProperty]
    public partial string? BusyMessage { get; set; }

    /// <summary>The last error, or <see langword="null"/> when the last operation succeeded.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Runs an operation with busy state, error capture and the view model's lifetime token.
    /// </summary>
    /// <param name="operation">Operation to run.</param>
    /// <param name="busyMessage">Message shown while it runs.</param>
    /// <returns><see langword="true"/> when the operation completed without throwing.</returns>
    protected async Task<bool> RunAsync(Func<CancellationToken, Task> operation, string busyMessage)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        BusyMessage = busyMessage;
        ErrorMessage = null;

        try
        {
            await operation(Lifetime).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Operation} failed.", busyMessage);
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        DisposeCore();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources a derived view model owns. Called after the lifetime token is cancelled
    /// and before it is disposed, so an override can unsubscribe from long lived services.
    /// </summary>
    protected virtual void DisposeCore()
    {
    }
}
