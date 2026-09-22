using System;
using System.Globalization;
using System.IO;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Velocity.Abstractions.Diagnostics;

namespace Velocity.Diagnostics.Logging;

/// <summary>
/// Wraps a Serilog formatter and redacts the rendered text before it is written.
/// </summary>
/// <remarks>
/// Redacting the rendered output rather than individual properties is deliberate: a message
/// template can embed a path in an exception's stack trace or an inner exception message, and
/// property level scrubbing misses those.
/// </remarks>
public sealed class RedactingTextFormatter : ITextFormatter
{
    private readonly ITextFormatter _inner;
    private readonly ISensitiveDataRedactor _redactor;

    /// <summary>Creates a formatter that redacts the output of <paramref name="inner"/>.</summary>
    /// <param name="inner">Formatter that produces the text.</param>
    /// <param name="redactor">Redactor applied to the produced text.</param>
    public RedactingTextFormatter(ITextFormatter inner, ISensitiveDataRedactor redactor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    /// <summary>Creates a formatter using the standard Velocity output template.</summary>
    /// <param name="redactor">Redactor applied to the produced text.</param>
    /// <returns>The formatter.</returns>
    public static RedactingTextFormatter CreateDefault(ISensitiveDataRedactor redactor) =>
        new(
            new MessageTemplateTextFormatter(
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                CultureInfo.InvariantCulture),
            redactor);

    /// <inheritdoc />
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        output.Write(_redactor.Redact(buffer.ToString()));
    }
}
