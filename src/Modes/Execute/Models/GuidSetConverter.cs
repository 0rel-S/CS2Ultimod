using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2Ultimod.Modes.Execute.Models;

/// <summary>
/// Converts a JSON array of Guid-or-int IDs into HashSet&lt;Guid&gt;.
/// Handles both formats: ["uuid-string", ...] and [1, 2, 3, ...].
/// </summary>
public sealed class GuidSetConverter : JsonConverter<HashSet<Guid>>
{
    private static readonly GuidOrIntConverter _item = new();

    public override HashSet<Guid> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array for HashSet<Guid>.");

        var set = new HashSet<Guid>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            set.Add(_item.Read(ref reader, typeof(Guid), options));
        }
        return set;
    }

    public override void Write(Utf8JsonWriter writer, HashSet<Guid> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var id in value)
            writer.WriteStringValue(id.ToString());
        writer.WriteEndArray();
    }
}
