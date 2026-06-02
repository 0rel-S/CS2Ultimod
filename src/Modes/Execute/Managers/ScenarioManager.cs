using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models;

namespace CS2Ultimod.Modes.Execute.Managers;

public sealed class ScenarioManager
{
    private static readonly Random _rng = new();

    private ExecuteMapConfig? _config;
    public ExecuteMapConfig? Config => _config;

    public Scenario? CurrentScenario { get; private set; }
    public bool IsForcingScenario   { get; private set; }

    public void SetConfig(ExecuteMapConfig? config)
    {
        _config = config;
        CurrentScenario = null;
        IsForcingScenario = false;
    }

    /// <summary>Returns true if a valid map config is loaded.</summary>
    public bool HasConfig => _config != null && _config.Scenarios.Count > 0;

    /// <summary>
    /// Picks a random scenario that has enough spawns for the current player count.
    /// </summary>
    public Scenario? PickRandom(int activePlayers)
    {
        if (_config == null) return null;
        if (IsForcingScenario)  return CurrentScenario;

        var valid = _config.Scenarios
            .Where(s => s.MinPlayerCount <= activePlayers)
            .Where(s =>
            {
                var total = s.Spawns[CsTeam.Terrorist].Count + s.Spawns[CsTeam.CounterTerrorist].Count;
                return total >= activePlayers;
            })
            .ToList();

        if (valid.Count == 0)
        {
            // Fall back to any scenario
            valid = _config.Scenarios.ToList();
        }

        if (valid.Count == 0)
        {
            CurrentScenario = null;
            return null;
        }

        CurrentScenario = valid[_rng.Next(valid.Count)];
        return CurrentScenario;
    }

    public void ForceScenario(Scenario? s)
    {
        CurrentScenario   = s;
        IsForcingScenario = s != null;
    }

    // ── Editor helpers ─────────────────────────────────────────────────────────

    public bool AddScenario(Scenario scenario)
    {
        if (_config == null) return false;
        if (_config.Scenarios.Any(s => s.Name == scenario.Name)) return false;
        _config.Scenarios.Add(scenario);
        return true;
    }

    public bool AddSpawnToScenario(string scenarioName, Guid spawnId)
    {
        if (_config == null) return false;

        var scenario = _config.Scenarios.FirstOrDefault(s => s.Name == scenarioName);
        var spawn    = _config.Spawns.FirstOrDefault(s => s.Id == spawnId);
        if (scenario == null || spawn == null) return false;

        scenario.SpawnIds.Add(spawnId);
        scenario.Spawns[spawn.Team].Add(spawn);
        return true;
    }

    public bool AddGrenadeToScenario(string scenarioName, Guid grenadeId)
    {
        if (_config == null) return false;

        var scenario = _config.Scenarios.FirstOrDefault(s => s.Name == scenarioName);
        var grenade  = _config.Grenades.FirstOrDefault(g => g.Id == grenadeId);
        if (scenario == null || grenade == null) return false;

        scenario.GrenadeIds.Add(grenadeId);
        scenario.Grenades[grenade.Team].Add(grenade);
        return true;
    }
}
