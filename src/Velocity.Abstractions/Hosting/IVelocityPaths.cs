using System.IO;

namespace Velocity.Abstractions.Hosting;

/// <summary>
/// Filesystem locations the product uses.
/// </summary>
/// <remarks>
/// <para>
/// Machine state lives under <c>%ProgramData%</c> because the privileged helper and the desktop UI
/// run as different principals and must read the same journal: putting the recovery journal under
/// the user profile would mean the helper cannot roll back a crashed transaction.
/// </para>
/// <para>
/// A portable build redirects every path under the application directory; nothing in the codebase
/// may compose paths from environment variables directly.
/// </para>
/// </remarks>
public interface IVelocityPaths
{
    /// <summary>Root directory for machine wide data.</summary>
    string DataDirectory { get; }

    /// <summary>Directory holding rolling log files.</summary>
    string LogDirectory { get; }

    /// <summary>Full path of the SQLite database file.</summary>
    string DatabaseFile { get; }

    /// <summary>Directory holding exported and imported snapshot bundles.</summary>
    string SnapshotDirectory { get; }

    /// <summary>Directory holding user editable JSON profiles.</summary>
    string ProfileDirectory { get; }

    /// <summary>Creates any missing directories.</summary>
    void EnsureCreated();
}
