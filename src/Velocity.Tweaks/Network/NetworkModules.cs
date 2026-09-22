using Velocity.Abstractions.Tweaks;

namespace Velocity.Tweaks.Network;

/// <summary>
/// Stops Windows powering the network adapter down.
/// </summary>
/// <remarks>
/// <b>Why this one is real.</b> When Windows is allowed to power an adapter down, the adapter has
/// to come back up, and that transition costs milliseconds at exactly the wrong moment. Unlike most
/// "network optimization" registry values, this one corresponds to a checkbox in Device Manager
/// that the user can see and to a behaviour that either happens or does not.
/// </remarks>
public sealed class AdapterPowerManagementTweak : NetworkKeywordTweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "network.adapter-power-management";

    /// <summary>Creates the module.</summary>
    public AdapterPowerManagementTweak()
        : base("PnPCapabilities", enabledValue: "0", disabledValue: "24", turnOff: true)
    {
    }

    /// <inheritdoc />
    public override TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Keep the network adapter powered",
        Category = TweakCategory.Network,
        Summary =
            "Stops Windows turning the network adapter off to save power, so it never has to wake " +
            "up mid-game.",
        TechnicalDescription =
            "Writes PnPCapabilities = 24 on the class key of the adapter carrying the default " +
            "route, which is the documented value for clearing both power management checkboxes in " +
            "Device Manager. Only two values of this keyword are interpreted by this product: 0 or " +
            "absent means power management is permitted, and 24 means both checkboxes are cleared. " +
            "Any other value is reported as unknown rather than decoded from bit positions.",
        ExpectedEffect =
            "Removes a source of occasional multi-millisecond stalls when the adapter transitions " +
            "out of a low power state. It does not reduce ping, because ping is distance to the " +
            "server.",
        Risk = RiskLevel.Low,
        Scope = TweakScope.Persistent,
        RequiresElevation = true,
        RequiresRestart = false,
        BenchmarkRecommended = false,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };
}

/// <summary>
/// Turns off Energy Efficient Ethernet on the adapter carrying the default route.
/// </summary>
/// <remarks>
/// <b>Why this one is real.</b> EEE puts the physical link into a low power idle state between
/// frames and takes time to bring it back. That wake time is latency added to the first packet
/// after a quiet moment, which in a game is a real pattern. The module only acts when the driver
/// publishes the keyword, because on most adapters it does not exist.
/// </remarks>
public sealed class EnergyEfficientEthernetTweak : NetworkKeywordTweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "network.energy-efficient-ethernet";

    /// <summary>Creates the module.</summary>
    public EnergyEfficientEthernetTweak()
        : base("*EEE", enabledValue: "1", disabledValue: "0", turnOff: true)
    {
    }

    /// <inheritdoc />
    public override TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Energy Efficient Ethernet",
        Category = TweakCategory.Network,
        Summary =
            "Turns off the link's low power idle state, so the first packet after a quiet moment " +
            "does not wait for the link to wake.",
        TechnicalDescription =
            "Writes the *EEE driver keyword to 0 on the class key of the adapter carrying the " +
            "default route. The module is offered only when the driver publishes *EEE; when it does " +
            "not, the setting does not exist on that adapter and nothing is written.",
        ExpectedEffect =
            "Removes wake latency on the physical link after idle periods. On a link that is never " +
            "idle it changes nothing. It does not reduce average ping.",
        Risk = RiskLevel.Low,
        Scope = TweakScope.Persistent,
        RequiresElevation = true,
        RequiresRestart = false,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };
}

/// <summary>
/// Turns off interrupt moderation on the adapter carrying the default route.
/// </summary>
/// <remarks>
/// <b>The trade this one makes, stated plainly.</b> Interrupt moderation batches packet interrupts
/// to reduce processor load. Turning it off means the processor is interrupted per packet: lower
/// per packet latency, higher CPU usage. On a machine that is already processor bound in the game,
/// that trade can make frame times worse. This is the clearest case in the product of a setting
/// that must be measured rather than assumed, and it is flagged accordingly.
/// </remarks>
public sealed class InterruptModerationTweak : NetworkKeywordTweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "network.interrupt-moderation";

    /// <summary>Creates the module.</summary>
    public InterruptModerationTweak()
        : base("*InterruptModeration", enabledValue: "1", disabledValue: "0", turnOff: true)
    {
    }

    /// <inheritdoc />
    public override TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Interrupt moderation",
        Category = TweakCategory.Network,
        Summary =
            "Stops the adapter batching interrupts, which lowers per packet latency and raises " +
            "processor load.",
        TechnicalDescription =
            "Writes the *InterruptModeration driver keyword to 0 on the class key of the adapter " +
            "carrying the default route. Offered only when the driver publishes the keyword. " +
            "With moderation off the adapter raises an interrupt per packet instead of batching " +
            "them, so packets are handled sooner at the cost of more processor time in the network " +
            "stack.",
        ExpectedEffect =
            "Lower network stack latency, higher CPU usage. On a machine that is already processor " +
            "bound in the game this can make frame times worse rather than better. Benchmark it; " +
            "this is the setting in the product most likely to be a net loss on the wrong machine.",
        Risk = RiskLevel.Moderate,
        Scope = TweakScope.Persistent,
        RequiresElevation = true,
        RequiresRestart = false,
        BenchmarkRecommended = true,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };
}
