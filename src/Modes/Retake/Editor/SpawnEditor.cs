using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Modes.Retake.Managers;
using CS2Ultimod.Modes.Retake.Models;

namespace CS2Ultimod.Modes.Retake.Editor;

public sealed class SpawnEditor
{
    private readonly RetakeSpawnManager _spawnManager;
    private readonly Dictionary<int, BombSite> _editingSite = [];
    private bool _commandsRegistered;

    public SpawnEditor(RetakeSpawnManager spawnManager) => _spawnManager = spawnManager;

    public bool IsEditing(CCSPlayerController player) => _editingSite.ContainsKey(player.Slot);

    public void RegisterCommands()
    {
        if (_commandsRegistered) return;
        _commandsRegistered = true;
        CS2UltimodPlugin.Commands.Register(new Core.Utils.ChatCommand(
            "edit", ["spawns"],
            "@cs2ultimod/edit", "<A|B>",
            OnEditCommand,
            [GameMode.Retake]));

        CS2UltimodPlugin.Commands.Register(new Core.Utils.ChatCommand(
            "add", ["addspawn", "newspawn", "new"],
            "@cs2ultimod/edit", "<CT|T> <Y|N> (can plant)",
            OnAddCommand,
            [GameMode.Retake]));

        CS2UltimodPlugin.Commands.Register(new Core.Utils.ChatCommand(
            "remove", ["removespawn", "deletespawn", "delete"],
            "@cs2ultimod/edit", "",
            OnRemoveCommand,
            [GameMode.Retake]));

        CS2UltimodPlugin.Commands.Register(new Core.Utils.ChatCommand(
            "nearest", ["nearestspawn"],
            "@cs2ultimod/edit", "",
            OnNearestCommand,
            [GameMode.Retake]));

        CS2UltimodPlugin.Commands.Register(new Core.Utils.ChatCommand(
            "done", ["hidespawns", "exitedit"],
            "@cs2ultimod/edit", "",
            OnDoneCommand,
            [GameMode.Retake]));
    }

    private void OnEditCommand(CCSPlayerController player, string[] args)
    {
        if (!CS2UltimodPlugin.Permissions.RequireFlag(player, "@cs2ultimod/edit")) return;

        var siteArg = args.Length > 0 ? args[0].ToUpper() : "A";
        var site = siteArg == "B" ? BombSite.B : BombSite.A;

        _editingSite[player.Slot] = site;

        var spawns = _spawnManager.GetSpawnsForSite(site);
        Chat.TellSuccess(player, $"Mode édition site {site} — {spawns.Count} spawns. !add, !remove, !nearest, !done");
        ShowSpawnsToPlayer(player, site);
    }

    private void OnAddCommand(CCSPlayerController player, string[] args)
    {
        if (!_editingSite.TryGetValue(player.Slot, out var site))
        {
            Chat.TellError(player, "Entrez d'abord en mode édition avec !edit A ou !edit B");
            return;
        }

        var teamArg = args.Length > 0 ? args[0].ToUpper() : "CT";
        var canPlant = args.Length > 1 && args[1].ToUpper() == "Y";

        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        var pos = pawn.AbsOrigin ?? new Vector(0, 0, 0);
        var ang = pawn.AbsRotation ?? new QAngle(0, 0, 0);

        var spawn = new RetakeSpawn
        {
            Team = teamArg == "T" ? 2 : 3,
            CanBePlanter = canPlant,
            VectorStr = $"{pos.X} {pos.Y} {pos.Z}",
            QAngleStr = $"{ang.X} {ang.Y} {ang.Z}"
        };

        _spawnManager.AddSpawn(spawn, site);
        Chat.TellSuccess(player, $"Spawn {teamArg} ajouté au site {site} (CanPlant: {canPlant})");
    }

    private void OnRemoveCommand(CCSPlayerController player, string[] args)
    {
        if (!_editingSite.TryGetValue(player.Slot, out var site))
        {
            Chat.TellError(player, "Entrez d'abord en mode édition avec !edit A ou !edit B");
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) return;

        _spawnManager.RemoveNearest(pawn.AbsOrigin, site);
        Chat.TellSuccess(player, $"Spawn le plus proche supprimé du site {site}");
    }

    private void OnNearestCommand(CCSPlayerController player, string[] args)
    {
        if (!_editingSite.TryGetValue(player.Slot, out var site))
        {
            Chat.TellError(player, "Entrez d'abord en mode édition.");
            return;
        }

        var spawns = _spawnManager.GetSpawnsForSite(site);
        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null || spawns.Count == 0) return;

        var nearest = spawns
            .OrderBy(s => VecDist(s.Position, pawn.AbsOrigin))
            .First();

        player.PlayerPawn.Value?.Teleport(nearest.Position, nearest.Angle, new Vector(0, 0, 0));
        Chat.TellSuccess(player, $"TP au spawn le plus proche ({(CsTeam)nearest.Team}, CanPlant:{nearest.CanBePlanter})");
    }

    private void OnDoneCommand(CCSPlayerController player, string[] args)
    {
        _editingSite.Remove(player.Slot);
        Chat.TellSuccess(player, "Mode édition terminé.");
    }

    public void OnPlayerDisconnected(CCSPlayerController player)
        => _editingSite.Remove(player.Slot);

    private void ShowSpawnsToPlayer(CCSPlayerController player, BombSite site)
    {
        var spawns = _spawnManager.GetSpawnsForSite(site);
        Chat.Tell(player, $"Site {site} — {spawns.Count} spawn(s) :");
        foreach (var s in spawns.Take(20))
            Chat.Tell(player, $"  [{(CsTeam)s.Team}] {s.VectorStr} (CanPlant:{s.CanBePlanter})");
    }

    private static float VecDist(Vector a, Vector b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}
