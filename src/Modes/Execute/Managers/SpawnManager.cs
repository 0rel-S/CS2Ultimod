using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Modes.Execute.Models;

namespace CS2Ultimod.Modes.Execute.Managers;

public sealed class SpawnManager
{
    private static readonly Random _rng = new();

    /// <summary>
    /// Teleports each valid player in the given lists to a random available spawn.
    /// T-spawns for Terrorists, CT-spawns for Counter-Terrorists.
    /// </summary>
    public void SetupSpawns(Scenario scenario)
    {
        var players = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.PawnIsAlive &&
                        (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist))
            .ToList();

        if (players.Count == 0) return;

        // Work on copies so we can pop spawns without modifying the scenario
        var tSpawns  = new List<Spawn>(scenario.Spawns[CsTeam.Terrorist]);
        var ctSpawns = new List<Spawn>(scenario.Spawns[CsTeam.CounterTerrorist]);

        foreach (var player in players)
        {
            var pool = player.Team == CsTeam.Terrorist ? tSpawns : ctSpawns;
            if (pool.Count == 0) continue;

            var idx   = _rng.Next(pool.Count);
            var spawn = pool[idx];
            pool.RemoveAt(idx);

            TeleportToSpawn(player, spawn);
        }
    }

    /// <summary>
    /// Degraded mode: teleport Ts to arbitrary positions and give full util.
    /// Called when no map config exists.
    /// </summary>
    public void SetupDegradedMode(IEnumerable<CCSPlayerController> players)
    {
        foreach (var player in players.Where(p => p.IsValid && p.PawnIsAlive))
        {
            if (player.Team == CsTeam.Terrorist)
                GiveFullUtil(player);
        }
    }

    private static void TeleportToSpawn(CCSPlayerController player, Spawn spawn)
    {
        if (spawn.Position == null || spawn.Angle == null) return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        pawn.Teleport(spawn.Position, spawn.Angle, new Vector(0, 0, 0));
    }

    /// <summary>Gives a player a full CT grenade kit.</summary>
    public static void GiveFullUtil(CCSPlayerController player)
    {
        player.GiveNamedItem("weapon_smokegrenade");
        player.GiveNamedItem("weapon_flashbang");
        player.GiveNamedItem("weapon_flashbang");
        player.GiveNamedItem("weapon_hegrenade");
        player.GiveNamedItem("weapon_molotov");
    }
}
