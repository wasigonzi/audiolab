using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velocity.Data;

/// <summary>Serialization settings shared by every JSON document the product stores or exports.</summary>
/// <remarks>
/// Enums are written as strings so that a stored document stays readable and, more importantly,
/// survives a future reordering of an enum. Snake case matches the column naming in the schema.
/// </remarks>
public static class VelocityJson
{
    /// <summary>Options used for database documents and exported profiles and snapshots.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Options used when a human will read the output, such as an exported bundle.</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = new(Options)
    {
        WriteIndented = true,
    };
}
