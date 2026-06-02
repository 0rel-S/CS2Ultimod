using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models;

namespace CS2Ultimod.Modes.Execute;

/// <summary>
/// Root of a per-map JSON config file.
/// Supports both bazookaCodes (Guid IDs) and zwolof/mavproductions (int IDs) formats.
/// </summary>
public sealed class ExecuteMapConfig
{
    public List<Spawn>    Spawns    { get; set; } = new();
    public List<Grenade>  Grenades  { get; set; } = new();
    public List<Scenario> Scenarios { get; set; } = new();

    // ── Serialisation helpers ──────────────────────────────────────────────────

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented    = true,
        Converters       =
        {
            new VectorJsonConverter(),
            new QAngleJsonConverter(),
            new GuidOrIntConverter(),
            new NullableGuidOrIntConverter(),
            new GuidSetConverter()
        }
    };

    /// <summary>Loads config from disk, returns null on failure.</summary>
    public static ExecuteMapConfig? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var cfg  = JsonSerializer.Deserialize<ExecuteMapConfig>(json, JsonOptions);
            cfg?.ParseIdReferences();
            return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Execute] Failed to load config '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves config to disk.</summary>
    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Execute] Failed to save config '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves SpawnIds / GrenadeIds in each scenario to the actual Spawn / Grenade objects.
    /// Call this after deserialisation.
    /// </summary>
    public void ParseIdReferences()
    {
        var spawnById   = Spawns.ToDictionary(s => s.Id);
        var grenadeById = Grenades.ToDictionary(g => g.Id);

        foreach (var scenario in Scenarios)
        {
            scenario.Spawns[CsTeam.Terrorist]        = new();
            scenario.Spawns[CsTeam.CounterTerrorist] = new();
            scenario.Grenades[CsTeam.Terrorist]        = new();
            scenario.Grenades[CsTeam.CounterTerrorist] = new();

            foreach (var id in scenario.SpawnIds)
            {
                if (spawnById.TryGetValue(id, out var s))
                    scenario.Spawns[s.Team].Add(s);
                else
                    Console.WriteLine($"[Execute] Scenario '{scenario.Name}': spawn id {id} not found.");
            }

            foreach (var id in scenario.GrenadeIds)
            {
                if (grenadeById.TryGetValue(id, out var g))
                    scenario.Grenades[g.Team].Add(g);
                else
                    Console.WriteLine($"[Execute] Scenario '{scenario.Name}': grenade id {id} not found.");
            }
        }
    }
}
