using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2Ultimod.Modes.Execute.Models;

/// <summary>
/// JSON converter that accepts both Guid strings (bazookaCodes format) and integer IDs
/// (zwolof/mavproductions format). Integers are converted to deterministic Guids using
/// Guid version 5-style name hashing so that the same integer always yields the same Guid.
/// </summary>
public sealed class GuidOrIntConverter : JsonConverter<Guid>
{
    // Namespace UUID used as base for deterministic int→Guid conversion (arbitrary, fixed).
    private static readonly Guid Namespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    public static Guid FromInt(int id)
    {
        // Deterministic: hash the namespace + 4-byte big-endian integer id
        var nameBytes = new byte[20];
        var ns = Namespace.ToByteArray();
        Buffer.BlockCopy(ns, 0, nameBytes, 0, 16);
        nameBytes[16] = (byte)(id >> 24);
        nameBytes[17] = (byte)(id >> 16);
        nameBytes[18] = (byte)(id >> 8);
        nameBytes[19] = (byte)id;

        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(nameBytes);

        // Construct a version-5 UUID from the hash
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant RFC 4122

        return new Guid(
            (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3],
            (short)((hash[4] << 8) | hash[5]),
            (short)((hash[6] << 8) | hash[7]),
            hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
    }

    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var id = reader.GetInt32();
            return FromInt(id);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (Guid.TryParse(s, out var g))
                return g;
            throw new JsonException($"Cannot parse Guid from string '{s}'.");
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} for Guid.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>Nullable variant of the converter.</summary>
public sealed class NullableGuidOrIntConverter : JsonConverter<Guid?>
{
    private static readonly GuidOrIntConverter _inner = new();

    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        return _inner.Read(ref reader, typeof(Guid), options);
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToString());
    }
}
