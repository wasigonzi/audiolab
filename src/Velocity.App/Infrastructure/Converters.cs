using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Velocity.Abstractions.Tweaks;

namespace Velocity.App;

/// <summary>Shows an element only when the bound boolean is true.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool boolean && boolean;

        if (parameter is string text && string.Equals(text, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when the bound string has content.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Turns a 0-1 fraction into a percentage for a progress bar.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double fraction ? Math.Clamp(fraction, 0d, 1d) * 100d : 0d;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a risk level into a colour key, so the list communicates risk without a wall of text.
/// </summary>
public sealed class RiskToBrushKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is RiskLevel risk
            ? risk switch
            {
                RiskLevel.Safe or RiskLevel.Low => "PositiveBrush",
                RiskLevel.Moderate => "CautionBrush",
                _ => "NegativeBrush",
            }
            : "TextSecondaryBrush";

        return Application.Current.Resources[key];
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Renders a compatibility status as the sentence the user reads.</summary>
public sealed class CompatibilityToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CompatibilityStatus status
            ? status switch
            {
                CompatibilityStatus.Supported => "Available on this system",
                CompatibilityStatus.UnsupportedHardware => "Your hardware does not have this feature",
                CompatibilityStatus.UnsupportedOperatingSystem => "Not supported on this Windows build",
                CompatibilityStatus.ElevationUnavailable => "Needs the Velocity helper service",
                CompatibilityStatus.AlreadyOptimal => "Already configured this way",
                CompatibilityStatus.Blocked => "Deliberately not offered",
                _ => "Could not be determined",
            }
            : string.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
