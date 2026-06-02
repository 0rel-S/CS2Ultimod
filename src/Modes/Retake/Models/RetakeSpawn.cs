using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json.Serialization;

namespace CS2Ultimod.Modes.Retake.Models;

public enum BombSite { A, B }

public sealed class RetakeSpawn
{
    [JsonPropertyName("Team")]
    public int Team { get; set; }  // 2 = T, 3 = CT

    [JsonPropertyName("CanBePlanter")]
    public bool CanBePlanter { get; set; }

    [JsonPropertyName("Vector")]
    public string VectorStr { get; set; } = "0 0 0";

    [JsonPropertyName("QAngle")]
    public string QAngleStr { get; set; } = "0 0 0";

    [JsonIgnore]
    public CsTeam CsTeam => (CsTeam)Team;

    [JsonIgnore]
    public Vector Position => ParseVector(VectorStr);

    [JsonIgnore]
    public QAngle Angle => ParseAngle(QAngleStr);

    private static Vector ParseVector(string s)
    {
        var parts = s.Split(' ');
        return parts.Length == 3
            ? new Vector(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                         float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                         float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture))
            : new Vector(0, 0, 0);
    }

    private static QAngle ParseAngle(string s)
    {
        var parts = s.Split(' ');
        return parts.Length == 3
            ? new QAngle(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                         float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                         float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture))
            : new QAngle(0, 0, 0);
    }
}

public sealed class MapSpawnConfig
{
    [JsonPropertyName("A")]
    public List<RetakeSpawn> A { get; set; } = [];

    [JsonPropertyName("B")]
    public List<RetakeSpawn> B { get; set; } = [];

    public List<RetakeSpawn> ForSite(BombSite site) => site == BombSite.A ? A : B;
}
