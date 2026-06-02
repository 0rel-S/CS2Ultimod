using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Modes.Execute;
using CS2Ultimod.Modes.Retake;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Features.Allocator;

public static class AllocatorModule
{
    private static WeaponAllocator? _allocator;
    private static AllocatorMenu? _menu;
    private static readonly RoundTypeManager _roundType = new();

    public static RoundType CurrentRoundType => _roundType.GetCurrentRoundType();

    public static void Register()
    {
        // Migration is registered in CS2UltimodPlugin.Load() to keep version order explicit.
        _allocator = new WeaponAllocator(CS2UltimodPlugin.Database);
        _menu = new AllocatorMenu(_allocator);

        // Hook into RetakeMode allocation delegate
        RetakeMode.OnAllocatePlayer = async (player, site) =>
        {
            var rt = _roundType.GetCurrentRoundType(player.Team);
            await _allocator.AllocateAsync(player, rt);
        };

        // Hook into ExecuteMode allocation — same Pistol/Half/Full rotation as Retake.
        ExecuteMode.OnAllocate = player =>
        {
            var rt = _roundType.GetCurrentRoundType(player.Team);
            _ = _allocator.AllocateAsync(player, rt);
        };

        // Round end → advance round type + avance le round AWP. On ne tire PAS les
        // carriers ici : à RoundEnd le GameManager fait des SwitchTeam (promotion CT→T,
        // balance, scramble), donc p.Team est instable et ne correspond pas aux équipes
        // du round suivant. Le tirage est différé jusqu'à la 1ère allocation du round
        // suivant, quand les joueurs sont spawnés dans leurs équipes définitives.
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(evt =>
        {
            _roundType.OnRoundEnd();
            _allocator?.AdvanceRound();
        }, GameMode.Retake, GameMode.Execute, GameMode.Mixte);

        // Mode enter → reset round type + reset carriers AWP + recharge le cache opt-in.
        CS2UltimodPlugin.ModeManager.OnModeChanged += (prev, next) =>
        {
            if (next is GameMode.Retake or GameMode.Execute or GameMode.Mixte)
            {
                _roundType.OnModeEnter();
                _allocator.ResetAwpCarriers();
                _ = _allocator.LoadWantAwpCacheAsync();
            }
        };

        // Charge le cache opt-in dès le boot (le mode courant peut déjà être actif).
        _ = _allocator.LoadWantAwpCacheAsync();

        // !guns command
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "guns", ["gun", "weapons", "g"],
            null, "",
            (player, _) => _menu?.Open(player),
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte]));

        // Raccourcis !ak, !m4, etc. — équivalents direct du passage par le menu
        // !guns. Toujours appliqués à l'équipe courante du joueur ; refusés si
        // l'arme n'existe pas pour cette équipe (ex: !ak côté CT).
        RegisterWeaponShortcut("ak",     null,             "AK-47",     "FullBuyPrimary", CsItem.AK47,    CsTeam.Terrorist);
        RegisterWeaponShortcut("sg",     ["krieg"],        "SG 553",    "FullBuyPrimary", CsItem.SG553,   CsTeam.Terrorist);
        RegisterWeaponShortcut("m4",     ["m4a4"],         "M4A4",      "FullBuyPrimary", CsItem.M4A4,    CsTeam.CounterTerrorist);
        RegisterWeaponShortcut("m4a1",   ["m4s"],          "M4A1-S",    "FullBuyPrimary", CsItem.M4A1S,   CsTeam.CounterTerrorist);
        RegisterWeaponShortcut("aug",    null,             "AUG",       "FullBuyPrimary", CsItem.AUG,     CsTeam.CounterTerrorist);
        RegisterWeaponShortcut("deagle", null,             "Desert Eagle", "Secondary",   CsItem.Deagle,  null);
        RegisterWeaponShortcut("p250",   null,             "P250",      "Secondary",      CsItem.P250,    null);
        RegisterWeaponShortcut("cz",     null,             "CZ75-Auto", "Secondary",      CsItem.CZ,      null);
        RegisterWeaponShortcut("usp",    null,             "USP-S",     "Secondary",      CsItem.USPS,    CsTeam.CounterTerrorist);
        RegisterWeaponShortcut("tec",    ["tec9"],         "Tec-9",     "Secondary",      CsItem.Tec9,    CsTeam.Terrorist);

        // !awp — toggle de la queue AWP pour l'équipe courante (équivalent du
        // bouton "AWP : ON/OFF" dans le menu Full-Achat de l'équipe).
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "awp", null, null, "",
            (player, _) => HandleAwpToggle(player),
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte]));

        // !rounds — admin only, opens the round-type config menu
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "rounds", ["roundtype", "rt"],
            "@css/root", "",
            (player, _) => OpenRoundsMenu(player),
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte]));
    }

    // requiredTeam == null → arme dispo pour les deux équipes, on écrit la pref
    // sur l'équipe courante du joueur. Sinon on rejette si l'équipe ne matche pas.
    private static void RegisterWeaponShortcut(
        string name, string[]? aliases, string display,
        string slot, CsItem item, CsTeam? requiredTeam)
    {
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            name, aliases, null, "",
            (player, _) => HandleWeaponShortcut(player, display, slot, item, requiredTeam),
            [GameMode.Retake, GameMode.Execute, GameMode.Mixte]));
    }

    private static void HandleWeaponShortcut(
        CCSPlayerController player, string display, string slot, CsItem item, CsTeam? requiredTeam)
    {
        if (!player.IsValid || _allocator == null) return;
        var team = player.Team;
        if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
        {
            Chat.TellError(player, "Rejoins une équipe pour configurer ton arme.");
            return;
        }
        if (requiredTeam != null && team != requiredTeam)
        {
            var teamName = requiredTeam == CsTeam.Terrorist ? "côté T" : "côté CT";
            Chat.TellError(player, $"{display} n'est dispo que {teamName}.");
            return;
        }
        var steamId = player.SteamID;
        var capturedTeam = team;
        _ = Task.Run(async () =>
        {
            try { await _allocator.SetPrefAsync(steamId, capturedTeam, slot, item); }
            catch (Exception ex)
            {
                CS2UltimodPlugin.Log?.LogError(ex,
                    "[Allocator] shortcut SetPrefAsync failed steam={Steam} team={Team} slot={Slot} weapon={Weapon}",
                    steamId, capturedTeam, slot, item);
            }
        });
        Chat.TellSuccess(player, $"Arme enregistrée : {display}");
    }

    private static void HandleAwpToggle(CCSPlayerController player)
    {
        if (!player.IsValid || _allocator == null) return;
        var team = player.Team;
        if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
        {
            Chat.TellError(player, "Rejoins une équipe pour configurer la queue AWP.");
            return;
        }
        var steamId = player.SteamID;
        _ = Task.Run(async () =>
        {
            try
            {
                var prefs = await _allocator.GetPrefsAsync(steamId, team);
                var newState = !prefs.ContainsKey("WantAwp");
                await _allocator.SetWantAwpAsync(steamId, team, newState);
                Server.NextFrame(() =>
                {
                    if (!player.IsValid) return;
                    Chat.TellSuccess(player, newState
                        ? "Queue AWP : \x04ON\x01 — tu seras dans le tirage AWP en full-buy."
                        : "Queue AWP : \x04OFF\x01 — tu auras toujours ton rifle préféré.");
                });
            }
            catch (Exception ex)
            {
                CS2UltimodPlugin.Log?.LogError(ex,
                    "[Allocator] !awp toggle failed steam={Steam} team={Team}", steamId, team);
            }
        });
    }

    private static void OpenRoundsMenu(CounterStrikeSharp.API.Core.CCSPlayerController player)
    {
        if (!player.IsValid) return;
        var m = CS2UltimodPlugin.Menus.Create($"Rounds — mode={_roundType.Mode}, freeze={(_roundType.Frozen?.ToString() ?? "off")}");
        m.AddItem("Mode: Sequential", _ => { _roundType.Mode = RoundMode.Sequential; _roundType.ResetToPistol(); Notify(player, "Sequential"); });
        m.AddItem("Mode: Random sym",  _ => { _roundType.Mode = RoundMode.RandomSymmetric; _roundType.OnModeEnter(); Notify(player, "Random symétrique"); });
        m.AddItem("Mode: Random mix",  _ => { _roundType.Mode = RoundMode.RandomMixed; _roundType.OnModeEnter(); Notify(player, "Random mixé"); });
        m.AddItem("Freeze: Pistol",    _ => { _roundType.Frozen = RoundType.Pistol; Notify(player, "freeze pistol"); });
        m.AddItem("Freeze: HalfBuy",   _ => { _roundType.Frozen = RoundType.HalfBuy; Notify(player, "freeze half"); });
        m.AddItem("Freeze: FullBuy",   _ => { _roundType.Frozen = RoundType.FullBuy; Notify(player, "freeze full"); });
        m.AddItem("Freeze: off",       _ => { _roundType.Frozen = null; Notify(player, "freeze off"); });
        m.Open(player);
    }

    private static void Notify(CounterStrikeSharp.API.Core.CCSPlayerController player, string what)
        => CS2Ultimod.Core.Utils.Chat.Broadcast($"{player.PlayerName} → rounds: \x04{what}\x01");
}
