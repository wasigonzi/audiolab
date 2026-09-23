using System;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// A stable, non identifying key for "this hardware, this Windows build, these drivers".
/// </summary>
/// <remarks>
/// <para>
/// Benchmark results are only comparable within one fingerprint. The fingerprint deliberately
/// contains no serial numbers, MAC addresses, machine name or user name: it is a hash of
/// structural facts, so it can later be used as a cloud database key without carrying personal
/// data off the machine.
/// </para>
/// </remarks>
public sealed record HardwareFingerprint
{
    /// <summary>Hash covering CPU model and topology shape.</summary>
    public required string CpuHash { get; init; }

    /// <summary>Hash covering installed GPUs and their driver versions.</summary>
    public required string GpuHash { get; init; }

    /// <summary>Hash covering memory capacity and module configuration.</summary>
    public required string MemoryHash { get; init; }

    /// <summary>Hash covering the Windows build and revision.</summary>
    public required string OsHash { get; init; }

    /// <summary>Combined hash of every component above.</summary>
    public required string CompositeHash { get; init; }

    /// <summary>Short form of <see cref="CompositeHash"/> for display in the UI.</summary>
    public string ShortId => CompositeHash.Length <= 12 ? CompositeHash : CompositeHash[..12];

    /// <summary>
    /// Determines whether benchmark results recorded under <paramref name="other"/> may be
    /// compared against results recorded under this fingerprint.
    /// </summary>
    /// <param name="other">Fingerprint recorded with an earlier measurement.</param>
    /// <returns><see langword="true"/> when the two describe the same measurement environment.</returns>
    public bool IsComparableTo(HardwareFingerprint? other) =>
        other is not null && string.Equals(CompositeHash, other.CompositeHash, StringComparison.Ordinal);
}
