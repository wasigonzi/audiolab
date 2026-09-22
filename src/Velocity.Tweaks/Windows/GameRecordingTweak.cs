using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.State;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Tweaks.Windows;

/// <summary>
/// Turns off Game Bar background recording for the current user.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it affects.</b> Two per user settings:
/// <c>HKCU\System\GameConfigStore!GameDVR_Enabled</c> and
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR!AppCaptureEnabled</c>. Together they
/// control whether Windows keeps a rolling capture buffer while a game runs.
/// </para>
/// <para>
/// <b>Why it is worth doing.</b> Background recording continuously encodes gameplay. That is real,
/// ongoing GPU and disk work happening while the game runs, and unlike most "debloat" claims it is
/// straightforwardly measurable: the capture pipeline is either running or it is not.
/// </para>
/// <para>
/// <b>What it does not do.</b> It does not disable Game Mode, which is a different setting and is
/// generally worth leaving on. It does not remove the Game Bar, uninstall anything, or touch any
/// service. It writes two per user values that the user can see and change in Settings.
/// </para>
/// <para>
/// <b>Honest about magnitude.</b> If the user never had background recording enabled, this module
/// reports that and changes nothing. That is the common case on a machine someone has already
/// tuned.
/// </para>
/// </remarks>
public sealed class GameRecordingTweak : ITweak
{
    /// <summary>Identifier used by profiles, the journal and benchmark history.</summary>
    public const string TweakId = "windows.game-recording";

    private static readonly StateKey GameDvrEnabled =
        RegistryKeys.Value(RegistryKeys.GameConfigStore, "GameDVR_Enabled");

    private static readonly StateKey AppCaptureEnabled = RegistryKeys.Value(
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");

    private static readonly StateKey[] Keys = [GameDvrEnabled, AppCaptureEnabled];

    /// <inheritdoc />
    public TweakDescriptor Descriptor { get; } = new()
    {
        Id = TweakId,
        Name = "Game Bar background recording",
        Category = TweakCategory.Windows,
        Summary =
            "Turns off the rolling capture buffer Windows keeps while you play. Game Mode is left " +
            "on; only the recording is disabled.",
        TechnicalDescription =
            @"Writes HKCU\System\GameConfigStore!GameDVR_Enabled and " +
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR!AppCaptureEnabled to 0 " +
            "(REG_DWORD). Both are per user settings that need no administrator rights and are " +
            "visible in Settings under Gaming. Game Mode (AutoGameModeEnabled) is a separate " +
            "setting and is not touched.",
        ExpectedEffect =
            "Background recording continuously encodes gameplay, which is real GPU and disk work " +
            "while the game runs. Turning it off removes that work. If it was already off, this " +
            "module reports so and changes nothing.",
        Risk = RiskLevel.Safe,
        Scope = TweakScope.Persistent,
        RequiresElevation = false,
        RequiresRestart = false,
        BenchmarkRecommended = false,
        MinimumWindowsBuild = 19041,
        DefinitionVersion = 1,
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<StateKey>> GetStateKeysAsync(
        TweakContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StateKey>>(Keys);

    /// <inheritdoc />
    public Task<CompatibilityResult> CheckCompatibilityAsync(
        TweakContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(CompatibilityResult.Supported("These are per user settings on every Windows build."));

    /// <inheritdoc />
    public async Task<TweakObservation> DetectAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyDictionary<StateKey, StateValue> values =
            await context.State.ReadManyAsync(Keys, cancellationToken).ConfigureAwait(false);

        bool recordingOn = values.Any(entry => IsEnabled(entry.Value));

        return new TweakObservation
        {
            State = recordingOn ? AppliedState.NotApplied : AppliedState.Applied,
            CurrentValueSummary = recordingOn
                ? "Background recording is on"
                : "Background recording is already off",
            RecommendedValueSummary = "Off",
            Details = values.ToDictionary(
                entry => entry.Key.ToString(),
                entry => entry.Value.ToDisplayString(),
                StringComparer.Ordinal),
        };
    }

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(TweakContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var changed = new List<StateKey>();

        foreach (StateKey key in Keys)
        {
            StateValue current = await context.State.ReadAsync(key, cancellationToken).ConfigureAwait(false);

            if (!IsEnabled(current))
            {
                continue;
            }

            await context.State
                .WriteAsync(key, StateValue.FromUInt32(0), cancellationToken)
                .ConfigureAwait(false);

            changed.Add(key);
        }

        return changed.Count == 0
            ? ApplyResult.NoChange("Background recording was already off.")
            : ApplyResult.Applied("Background recording turned off.", changed.ToArray());
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        TweakContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyDictionary<StateKey, StateValue> values =
            await context.State.ReadManyAsync(Keys, cancellationToken).ConfigureAwait(false);

        List<string> stillOn = values
            .Where(entry => IsEnabled(entry.Value))
            .Select(entry => entry.Key.ToString())
            .ToList();

        return stillOn.Count == 0
            ? VerificationResult.Verified("Both recording settings read back as off.")
            : VerificationResult.Mismatch($"Still enabled: {string.Join(", ", stillOn)}.");
    }

    /// <summary>
    /// A value is "enabled" only when it exists and is non-zero. An absent value means the user
    /// never enabled recording, which is off, not unknown.
    /// </summary>
    private static bool IsEnabled(StateValue value) => !value.IsAbsent && value.AsUInt32() is > 0;
}
