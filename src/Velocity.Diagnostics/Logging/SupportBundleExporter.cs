using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Hosting;
using Velocity.Abstractions.Diagnostics;

namespace Velocity.Diagnostics.Logging;

/// <summary>Packages logs into an archive the user can attach to a support request.</summary>
public interface ISupportBundleExporter
{
    /// <summary>Writes a support bundle.</summary>
    /// <param name="destinationDirectory">Directory the archive is written to.</param>
    /// <param name="cancellationToken">Token used to abort the export.</param>
    /// <returns>Full path of the archive that was written.</returns>
    Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ISupportBundleExporter"/>.
/// </summary>
/// <remarks>
/// Copies log files through the redactor on the way into the archive rather than archiving them
/// directly, so a bundle cannot contain personal data even if a log file predates a redaction rule.
/// </remarks>
public sealed class SupportBundleExporter : ISupportBundleExporter
{
    private readonly IVelocityPaths _paths;
    private readonly ISensitiveDataRedactor _redactor;

    /// <summary>Creates the exporter.</summary>
    /// <param name="paths">Product filesystem layout.</param>
    /// <param name="redactor">Redactor applied to every file that is archived.</param>
    public SupportBundleExporter(IVelocityPaths paths, ISensitiveDataRedactor redactor)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        string fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"velocity-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        string archivePath = Path.Combine(destinationDirectory, fileName);

        using var archiveStream = new FileStream(
            archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

        if (Directory.Exists(_paths.LogDirectory))
        {
            foreach (string logFile in Directory.EnumerateFiles(_paths.LogDirectory, "*.log"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddRedactedAsync(archive, logFile, cancellationToken).ConfigureAwait(false);
            }
        }

        return archivePath;
    }

    private async Task AddRedactedAsync(ZipArchive archive, string sourcePath, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            // Share the file: the rolling sink may still hold it open for writing.
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(source);
            content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A log file that cannot be read must not fail the whole export.
            return;
        }

        ZipArchiveEntry entry = archive.CreateEntry(
            Path.Combine("logs", Path.GetFileName(sourcePath)), CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        await writer.WriteAsync(_redactor.Redact(content).AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
