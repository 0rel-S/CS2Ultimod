using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models.Enums;

namespace CS2Ultimod.Modes.Execute.Models;

public sealed class Spawn
{
    [JsonConverter(typeof(GuidOrIntConverter))]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    [JsonConverter(typeof(VectorJsonConverter))]
    public Vector? Position { get; set; }

    [JsonConverter(typeof(QAngleJsonConverter))]
    public QAngle? Angle { get; set; }

    public CsTeam Team { get; set; }
    public ESpawnType Type { get; set; } = ESpawnType.Normal;
}
