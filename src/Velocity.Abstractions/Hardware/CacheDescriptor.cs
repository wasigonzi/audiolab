using System.Collections.Generic;

namespace Velocity.Abstractions.Hardware;

/// <summary>
/// One processor cache instance, including which logical processors share it.
/// </summary>
/// <remarks>
/// Last level cache sharing is the only documented signal Windows gives us for inferring AMD
/// CCD/CCX boundaries, so this record is load bearing for the scheduler module rather than
/// being informational trivia.
/// </remarks>
public sealed record CacheDescriptor
{
    /// <summary>Cache level (1, 2, 3 or 4).</summary>
    public required byte Level { get; init; }

    /// <summary>Cache content type.</summary>
    public required CacheKind Kind { get; init; }

    /// <summary>Total size of this cache instance in bytes.</summary>
    public required uint SizeBytes { get; init; }

    /// <summary>Cache line size in bytes.</summary>
    public required ushort LineSizeBytes { get; init; }

    /// <summary>Associativity, or <c>0xFF</c> for fully associative as reported by Windows.</summary>
    public required byte Associativity { get; init; }

    /// <summary>Processor group the sharing mask applies to.</summary>
    public required ushort GroupId { get; init; }

    /// <summary>Global indexes of the logical processors that share this cache instance.</summary>
    public required IReadOnlyList<int> SharedByLogicalProcessorIndexes { get; init; }
}
