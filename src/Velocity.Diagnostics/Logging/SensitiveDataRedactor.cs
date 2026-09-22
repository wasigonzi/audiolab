using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Velocity.Abstractions.Diagnostics;

namespace Velocity.Diagnostics.Logging;

/// <summary>
/// Replaces the personal data that realistically ends up in an optimizer's logs.
/// </summary>
/// <remarks>
/// <para>
/// The three things that leak from a tool like this are the user name (in every path under the
/// profile directory), adapter hardware addresses, and the machine name. All three are replaced
/// with stable tokens so that a support bundle stays useful for correlation without identifying
/// anyone.
/// </para>
/// <para>
/// Redaction is applied at the formatter, not at the call site, so a new module cannot leak
/// personal data by forgetting to call it.
/// </para>
/// </remarks>
public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _rules;

    /// <summary>Creates a redactor for the current user and machine.</summary>
    public SensitiveDataRedactor()
        : this(Environment.UserName, Environment.MachineName, GetUserProfileDirectory())
    {
    }

    /// <summary>Creates a redactor with explicit values, used by tests.</summary>
    /// <param name="userName">User name to redact.</param>
    /// <param name="machineName">Machine name to redact.</param>
    /// <param name="userProfileDirectory">User profile directory to redact.</param>
    public SensitiveDataRedactor(string? userName, string? machineName, string? userProfileDirectory)
    {
        var rules = new List<(Regex, string)>();

        // Order matters: the profile directory contains the user name, so it is replaced first.
        if (!string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            rules.Add((
                new Regex(Regex.Escape(userProfileDirectory), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                "<user-profile>"));
        }

        if (!string.IsNullOrWhiteSpace(userName) && userName.Length > 2)
        {
            rules.Add((
                new Regex($@"\b{Regex.Escape(userName)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                "<user>"));
        }

        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length > 2)
        {
            rules.Add((
                new Regex($@"\b{Regex.Escape(machineName)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                "<machine>"));
        }

        rules.Add((MacAddressPattern(), "<mac>"));
        _rules = rules;
    }

    /// <inheritdoc />
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result = text;
        for (int i = 0; i < _rules.Count; i++)
        {
            (Regex pattern, string replacement) = _rules[i];
            result = pattern.Replace(result, replacement);
        }

        return result;
    }

    private static string? GetUserProfileDirectory()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    // Matches 6 groups of two hex digits separated by '-' or ':'.
    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressPattern();
}
