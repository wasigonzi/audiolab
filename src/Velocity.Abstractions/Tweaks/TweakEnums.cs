namespace Velocity.Abstractions.Tweaks;

/// <summary>Sidebar category a tweak belongs to.</summary>
public enum TweakCategory
{
    /// <summary>Processor scheduling, affinity and CPU power behaviour.</summary>
    Cpu = 1,

    /// <summary>Graphics adapter configuration exposed by Windows or the vendor.</summary>
    Gpu = 2,

    /// <summary>Memory behaviour and background memory pressure.</summary>
    Memory = 3,

    /// <summary>Network adapter, stack and latency configuration.</summary>
    Network = 4,

    /// <summary>Storage configuration and background I/O.</summary>
    Storage = 5,

    /// <summary>Windows background workload reduction.</summary>
    Windows = 6,

    /// <summary>Windows service configuration.</summary>
    Services = 7,

    /// <summary>Background process priority, affinity and suspension.</summary>
    Processes = 8,

    /// <summary>Power plans and processor power policy.</summary>
    Power = 9,

    /// <summary>Thread scheduling, CPU sets and processor groups.</summary>
    Scheduler = 10,

    /// <summary>Latency analysis driven changes.</summary>
    Latency = 11,

    /// <summary>Input device configuration.</summary>
    Input = 12,

    /// <summary>Display mode and refresh rate configuration.</summary>
    Display = 13,

    /// <summary>Aggressive or unproven changes, never enabled by default.</summary>
    Experimental = 14,
}

/// <summary>How much a tweak can disrupt the machine if it goes wrong.</summary>
public enum RiskLevel
{
    /// <summary>Reversible, scoped to the current session, cannot affect boot or security.</summary>
    Safe = 0,

    /// <summary>Persistent but well understood and trivially reversible.</summary>
    Low = 1,

    /// <summary>Can change behaviour of unrelated software; reversible.</summary>
    Moderate = 2,

    /// <summary>Can affect stability or requires a restart to fully reverse.</summary>
    High = 3,

    /// <summary>Unproven on this hardware class; requires explicit opt in and a benchmark.</summary>
    Experimental = 4,
}

/// <summary>How long a change survives.</summary>
public enum TweakScope
{
    /// <summary>
    /// Applied for the duration of a gaming session and reverted when the session ends, including
    /// after a crash, through the recovery journal.
    /// </summary>
    Session = 0,

    /// <summary>Written to persistent system state and reverted only on explicit rollback.</summary>
    Persistent = 1,
}

/// <summary>Result of evaluating whether a tweak may run on this machine.</summary>
public enum CompatibilityStatus
{
    /// <summary>The tweak can run.</summary>
    Supported = 0,

    /// <summary>The hardware does not have the feature the tweak configures.</summary>
    UnsupportedHardware = 1,

    /// <summary>The Windows build does not support the setting, or no longer honours it.</summary>
    UnsupportedOperatingSystem = 2,

    /// <summary>Supported, but the privileged helper is required and unavailable.</summary>
    ElevationUnavailable = 3,

    /// <summary>The system is already in the state this tweak would produce.</summary>
    AlreadyOptimal = 4,

    /// <summary>Deliberately blocked, for example because it would weaken security.</summary>
    Blocked = 5,

    /// <summary>Compatibility could not be determined; the tweak is not offered.</summary>
    Unknown = 6,
}

/// <summary>Whether the machine currently matches the state the tweak produces.</summary>
public enum AppliedState
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>The machine is not in the tweaked state.</summary>
    NotApplied = 1,

    /// <summary>The machine is fully in the tweaked state.</summary>
    Applied = 2,

    /// <summary>Some but not all of the tweaked state is present.</summary>
    PartiallyApplied = 3,
}

/// <summary>Outcome of an apply attempt.</summary>
public enum ApplyOutcome
{
    /// <summary>The change was made.</summary>
    Applied = 0,

    /// <summary>Nothing needed to change.</summary>
    NoChangeRequired = 1,

    /// <summary>The change was made but needs a restart to take effect.</summary>
    AppliedPendingRestart = 2,

    /// <summary>The change failed and any partial work was rolled back.</summary>
    Failed = 3,

    /// <summary>The tweak refused to run; see the message.</summary>
    Skipped = 4,
}

/// <summary>Outcome of verifying that an apply actually took effect.</summary>
public enum VerificationStatus
{
    /// <summary>The machine reports the expected state.</summary>
    Verified = 0,

    /// <summary>The machine does not report the expected state.</summary>
    Mismatch = 1,

    /// <summary>The effect cannot be observed until the machine restarts.</summary>
    PendingRestart = 2,

    /// <summary>The tweak has no observable post condition beyond the write itself.</summary>
    NotVerifiable = 3,
}
