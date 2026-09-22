namespace Velocity.Abstractions.Diagnostics;

/// <summary>
/// Removes personal data from text before it reaches a log file, the audit table or a support
/// bundle.
/// </summary>
/// <remarks>
/// The contract lives in the abstractions assembly because both the logging pipeline and the audit
/// writer apply it, and neither should have to know about the other.
/// </remarks>
public interface ISensitiveDataRedactor
{
    /// <summary>Returns <paramref name="text"/> with personal data replaced by stable tokens.</summary>
    /// <param name="text">Text that may contain personal data.</param>
    /// <returns>The redacted text.</returns>
    string Redact(string text);
}
