using System;
using System.Threading.Tasks;

namespace Velocity.Presentation.Mvvm;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// View models never reference a UI framework's dispatcher directly. The WinUI layer supplies an
/// implementation backed by <c>DispatcherQueue</c>; tests supply one that runs inline, which is
/// what allows the entire view model layer to be tested without a message pump.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary><see langword="true"/> when the caller is already on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>Runs an action on the UI thread.</summary>
    /// <param name="action">Action to run.</param>
    /// <returns>A task that completes when the action has run.</returns>
    Task InvokeAsync(Action action);
}

/// <summary>An <see cref="IUiDispatcher"/> that runs work inline, for tests and headless hosts.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public bool IsOnUiThread => true;

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }
}
