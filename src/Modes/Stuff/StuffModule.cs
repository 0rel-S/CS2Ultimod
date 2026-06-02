using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Modes.Stuff;

public static class StuffModule
{
    // Last death position per player for near-death respawn
    private static readonly Dictionary<ulong, (float X, float Y, float Z)> _deathPositions = new();

    public static void Register()
    {
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "clear", null,
            null, "",
            OnClear,
            [GameMode.Stuff]));

        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "addbot", null,
            "@cs2ultimod/mode", "",
            OnAddBot,
            [GameMode.Stuff]));

        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "removebots", null,
            "@cs2ultimod/mode", "",
            (_, _) => Server.ExecuteCommand("bot_kick"),
            [GameMode.Stuff]));

        CS2UltimodPlugin.EventBus.Subscribe<PlayerDeathEvent>(OnPlayerDeath, GameMode.Stuff);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerSpawnEvent>(OnPlayerSpawn, GameMode.Stuff);
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(OnRoundStart, GameMode.Stuff);
    }

    private static void OnClear(CCSPlayerController player, string[] _)
    {
        var types = new[]
        {
            "smokegrenade_projectile", "hegrenade_projectile", "flashbang_projectile",
            "molotov_projectile", "incendiarygrenade_projectile", "inferno",
            "decoy_projectile",
        };

        var removed = 0;
        foreach (var type in types)
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(type))
            {
                entity.Remove();
                removed++;
            }
        }

        Chat.Tell(player, $"{removed} projectile(s) supprimé(s).");
    }

    private static void OnAddBot(CCSPlayerController player, string[] _)
    {
        Server.ExecuteCommand("bot_add_t");

        // Freeze the newly spawned bot on the next frame
        Server.NextFrame(() =>
        {
            foreach (var bot in Utilities.GetPlayers()
                         .Where(p => p.IsValid && p.IsBot && p.PawnIsAlive))
            {
                var pawn = bot.PlayerPawn.Value;
                if (pawn == null) continue;
                pawn.MoveType = MoveType_t.MOVETYPE_NONE;
            }
        });

        Chat.Tell(player, "Bot mannequin ajouté.");
    }

    private static void OnPlayerDeath(PlayerDeathEvent e)
    {
        var victim = e.Victim;
        if (!victim.IsValid || victim.IsBot) return;

        var pawn = victim.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) return;

        _deathPositions[victim.SteamID] = (pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
    }

    private static void OnPlayerSpawn(PlayerSpawnEvent e)
    {
        var player = e.Player;
        if (!player.IsValid || player.IsBot) return;

        if (!_deathPositions.TryGetValue(player.SteamID, out var pos)) return;
        _deathPositions.Remove(player.SteamID);

        Server.NextFrame(() =>
        {
            if (!player.IsValid || !player.PawnIsAlive) return;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null) return;

            pawn.Teleport(new Vector(pos.X, pos.Y, pos.Z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
        });
    }

    private static void OnRoundStart(RoundStartEvent _)
    {
        // Refresh money for all players every round
        foreach (var p in PlayerExt.AllConnected())
            p.InGameMoneyServices!.Account = 65535;
    }
}
