using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Velocity.Presentation.Mvvm;

namespace Velocity.App;

/// <summary>
/// Marshals view model callbacks onto the UI thread.
/// </summary>
/// <remarks>
/// The dispatcher queue only exists once a window has been created, so the adapter is registered
/// first and attached afterwards. Before it is attached it runs work inline, which is correct for
/// the startup path where there is no other thread yet.
/// </remarks>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private DispatcherQueue? _queue;

    /// <summary>Binds the adapter to a window's dispatcher queue.</summary>
    /// <param name="queue">Queue to marshal onto.</param>
    public void Attach(DispatcherQueue queue) => _queue = queue;

    /// <inheritdoc />
    public bool IsOnUiThread => _queue is null || _queue.HasThreadAccess;

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_queue is null || _queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bool queued = _queue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!queued)
        {
            completion.SetException(
                new InvalidOperationException("The UI thread is shutting down and cannot accept work."));
        }

        return completion.Task;
    }
}
