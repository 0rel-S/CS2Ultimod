using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Ultimod.Modes.Execute.Models;

/// <summary>
/// Converts QAngle to/from "X Y Z" JSON strings.
/// </summary>
public sealed class QAngleJsonConverter : JsonConverter<QAngle?>
{
    public override QAngle? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a string value for QAngle.");

        var raw = reader.GetString() ?? throw new JsonException("QAngle string is null.");
        var cleaned = raw.Replace(",", "");
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3)
            throw new JsonException($"QAngle '{raw}' must be 'X Y Z' (got {parts.Length} parts).");

        if (!float.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var z))
            throw new JsonException($"Cannot parse QAngle floats from '{raw}'.");

        return new QAngle(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, QAngle? value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.ToString());
    }
}
