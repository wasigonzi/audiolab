using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;

namespace Velocity.Abstractions.Tweaks;

/// <summary>
/// Everything a tweak is given when it runs.
/// </summary>
/// <remarks>
/// A tweak receives no service provider and no ambient singletons. Everything it can reach is on
/// this object, which is what makes tweaks unit testable against a fabricated machine: the tests
/// in <c>Velocity.Core.Tests</c> run real tweak code against captured topology fixtures and an
/// in-memory state accessor, with no Windows involved.
/// </remarks>
public sealed class TweakContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="profile">The machine the tweak is running against.</param>
    /// <param name="cpuLayout">Interpreted processor layout for <paramref name="profile"/>.</param>
    /// <param name="state">The state accessor the tweak must use for all reads and writes.</param>
    /// <param name="privileges">Privileges available to the current process.</param>
    /// <param name="logger">Logger scoped to the tweak.</param>
    /// <param name="options">Profile supplied options for this tweak.</param>
    public TweakContext(
        SystemProfile profile,
        CpuLayout cpuLayout,
        IStateAccessor state,
        IPrivilegeContext privileges,
        ILogger logger,
        IReadOnlyDictionary<string, string>? options = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        CpuLayout = cpuLayout ?? throw new ArgumentNullException(nameof(cpuLayout));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The machine the tweak is running against.</summary>
    public SystemProfile Profile { get; }

    /// <summary>Interpreted processor layout.</summary>
    public CpuLayout CpuLayout { get; }

    /// <summary>State accessor to use for every read and write.</summary>
    public IStateAccessor State { get; }

    /// <summary>Privileges available to the current process.</summary>
    public IPrivilegeContext Privileges { get; }

    /// <summary>Logger scoped to this tweak.</summary>
    public ILogger Logger { get; }

    /// <summary>Options supplied by the active optimization profile.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>
    /// Data stored by a tweak during apply and handed back to <see cref="ICustomRollback"/>.
    /// Persisted with the transaction so that rollback survives a crash.
    /// </summary>
    public string? RollbackPayload { get; private set; }

    /// <summary>Stores data needed to reverse a non state based effect.</summary>
    /// <param name="payload">Serialized payload, typically JSON. Kept small.</param>
    public void SetRollbackPayload(string? payload) => RollbackPayload = payload;

    /// <summary>Reads an option as an integer.</summary>
    /// <param name="name">Option name.</param>
    /// <param name="defaultValue">Value to use when the option is absent or malformed.</param>
    /// <returns>The option value.</returns>
    public int GetOption(string name, int defaultValue) =>
        Options.TryGetValue(name, out string? raw) &&
        int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;

    /// <summary>Reads an option as a boolean.</summary>
    /// <param name="name">Option name.</param>
    /// <param name="defaultValue">Value to use when the option is absent or malformed.</param>
    /// <returns>The option value.</returns>
    public bool GetOption(string name, bool defaultValue) =>
        Options.TryGetValue(name, out string? raw) && bool.TryParse(raw, out bool parsed)
            ? parsed
            : defaultValue;

    /// <summary>Reads an option as a string.</summary>
    /// <param name="name">Option name.</param>
    /// <param name="defaultValue">Value to use when the option is absent.</param>
    /// <returns>The option value.</returns>
    public string GetOption(string name, string defaultValue) =>
        Options.TryGetValue(name, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : defaultValue;
}
