using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models.Enums;

namespace CS2Ultimod.Modes.Execute.Models;

public sealed class Scenario
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "Execute the {{site}} site!";
    public EBombsite Bombsite { get; set; } = EBombsite.Unknown;
    public bool DisableOtherBombsite { get; set; } = true;
    public int RoundTime { get; set; } = 90;
    public int MinPlayerCount { get; set; } = 2;

    /// <summary>Spawn IDs referenced by this scenario (may be Guid or int in JSON).</summary>
    [JsonConverter(typeof(GuidSetConverter))]
    public HashSet<Guid> SpawnIds { get; set; } = new();

    /// <summary>Grenade IDs referenced by this scenario (may be Guid or int in JSON).</summary>
    [JsonConverter(typeof(GuidSetConverter))]
    public HashSet<Guid> GrenadeIds { get; set; } = new();

    /// <summary>Resolved spawn lists — populated after ParseIdReferences(), not serialised.</summary>
    [JsonIgnore]
    public Dictionary<CsTeam, List<Spawn>> Spawns { get; set; } = new()
    {
        [CsTeam.Terrorist] = new(),
        [CsTeam.CounterTerrorist] = new()
    };

    /// <summary>Resolved grenade lists — populated after ParseIdReferences(), not serialised.</summary>
    [JsonIgnore]
    public Dictionary<CsTeam, List<Grenade>> Grenades { get; set; } = new()
    {
        [CsTeam.Terrorist] = new(),
        [CsTeam.CounterTerrorist] = new()
    };
}
