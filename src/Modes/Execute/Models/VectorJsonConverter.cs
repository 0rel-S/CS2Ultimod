using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Ultimod.Modes.Execute.Models;

/// <summary>
/// Converts Vector to/from "X Y Z" JSON strings.
/// Also tolerates thousands-separator commas in floats (e.g. "1,030.70 -1,216.11 -36.80")
/// as found in the zwolof/mavproductions map configs.
/// </summary>
public sealed class VectorJsonConverter : JsonConverter<Vector?>
{
    public override Vector? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a string value for Vector.");

        var raw = reader.GetString() ?? throw new JsonException("Vector string is null.");
        return ParseVector(raw);
    }

    public static Vector ParseVector(string raw)
    {
        // Strip thousands-separator commas that appear in mavproductions JSONs ("1,030.70" -> "1030.70")
        var cleaned = raw.Replace(",", "");
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3)
            throw new JsonException($"Vector '{raw}' must be 'X Y Z' (got {parts.Length} parts).");

        if (!float.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var z))
            throw new JsonException($"Cannot parse Vector floats from '{raw}'.");

        return new Vector(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vector? value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.ToString());
    }
}
