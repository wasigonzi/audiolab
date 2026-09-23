using System;

namespace Velocity.Presentation.Mvvm;

/// <summary>How much of the product's complexity is shown.</summary>
public enum ApplicationMode
{
    /// <summary>One click optimization with a profile choice and nothing else.</summary>
    Easy = 0,

    /// <summary>Individual tweaks with their effect, risk and current state.</summary>
    Advanced = 1,

    /// <summary>
    /// Everything Advanced shows, plus the registry location or API behind each tweak, the captured
    /// original value, the processor masks involved and the rollback record.
    /// </summary>
    Expert = 2,
}

/// <summary>Tracks the mode the user has chosen.</summary>
public interface IApplicationModeService
{
    /// <summary>The current mode.</summary>
    ApplicationMode Mode { get; }

    /// <summary>Raised when the mode changes.</summary>
    event EventHandler<ApplicationMode>? ModeChanged;

    /// <summary>Changes the mode.</summary>
    /// <param name="mode">Mode to switch to.</param>
    void SetMode(ApplicationMode mode);
}

/// <summary>Default <see cref="IApplicationModeService"/>.</summary>
public sealed class ApplicationModeService : IApplicationModeService
{
    /// <inheritdoc />
    public ApplicationMode Mode { get; private set; } = ApplicationMode.Easy;

    /// <inheritdoc />
    public event EventHandler<ApplicationMode>? ModeChanged;

    /// <inheritdoc />
    public void SetMode(ApplicationMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        ModeChanged?.Invoke(this, mode);
    }
}
