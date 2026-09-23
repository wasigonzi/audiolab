using System;
using System.IO;
using Velocity.Abstractions.Hosting;

namespace Velocity.Diagnostics;

/// <summary>
/// Default implementation of <see cref="IVelocityPaths"/>.
/// </summary>
/// <remarks>
/// Installed builds place data under <c>%ProgramData%\Velocity</c>. Portable builds and tests pass
/// an explicit root, which is also how the test suite keeps every run isolated.
/// </remarks>
public sealed class VelocityPaths : IVelocityPaths
{
    private const string ProductFolderName = "Velocity";

    /// <summary>Creates paths rooted at the machine wide data directory.</summary>
    public VelocityPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProductFolderName))
    {
    }

    /// <summary>Creates paths rooted at an explicit directory.</summary>
    /// <param name="dataDirectory">Root directory for all product data.</param>
    public VelocityPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <inheritdoc />
    public string DatabaseFile => Path.Combine(DataDirectory, "velocity.db");

    /// <inheritdoc />
    public string SnapshotDirectory => Path.Combine(DataDirectory, "snapshots");

    /// <inheritdoc />
    public string ProfileDirectory => Path.Combine(DataDirectory, "profiles");

    /// <inheritdoc />
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SnapshotDirectory);
        Directory.CreateDirectory(ProfileDirectory);
    }
}
