using System;
using Microsoft.Extensions.Logging;

namespace Velocity.Core.Tweaks;

/// <summary>Source generated log messages for the tweak engine.</summary>
internal static partial class TweakLoggerExtensions
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Compatibility check for tweak {TweakId} threw; the tweak will not be offered.")]
    internal static partial void LogTweakCompatibilityFailure(this ILogger logger, string tweakId, Exception exception);
}
