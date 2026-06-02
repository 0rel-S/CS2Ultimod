using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Utils;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Features.Allocator;

public sealed class AllocatorMenu
{
    private readonly WeaponAllocator _allocator;

    public AllocatorMenu(WeaponAllocator allocator) => _allocator = allocator;

    public void Open(CCSPlayerController player)
    {
        _ = Task.Run(async () =>
        {
            var tPrefs  = await _allocator.GetPrefsAsync(player.SteamID, CsTeam.Terrorist);
            var ctPrefs = await _allocator.GetPrefsAsync(player.SteamID, CsTeam.CounterTerrorist);
            Server.NextFrame(() =>
            {
                if (!player.IsValid) return;
                BuildAndOpen(player, tPrefs, ctPrefs);
            });
        });
    }

    private void BuildAndOpen(
        CCSPlayerController player,
        Dictionary<string, CsItem> tPrefs,
        Dictionary<string, CsItem> ctPrefs)
    {
        // Level 1 — root
        var root = CS2UltimodPlugin.Menus.Create("Préférences d'armes");

        // Level 2 — T
        var tMenu = CS2UltimodPlugin.Menus.Create("Terroriste");
        var tFull = BuildFullBuyMenu("T Full-Achat", player, CsTeam.Terrorist, TFullBuy, tPrefs);
        var tHalf = BuildWeaponMenu("T Semi-Achat", player, CsTeam.Terrorist, "HalfBuyPrimary", THalfBuy, tPrefs);
        var tSec  = BuildWeaponMenu("T Secondaire",  player, CsTeam.Terrorist, "Secondary",       TSecondaries, tPrefs);
        tMenu.AddSubmenu("Full-Achat", tFull);
        tMenu.AddSubmenu("Semi-Achat", tHalf);
        tMenu.AddSubmenu("Secondaire",  tSec);

        // Level 2 — CT
        var ctMenu = CS2UltimodPlugin.Menus.Create("Anti-Terroriste");
        var ctFull = BuildFullBuyMenu("CT Full-Achat", player, CsTeam.CounterTerrorist, CTFullBuy, ctPrefs);
        var ctHalf = BuildWeaponMenu("CT Semi-Achat", player, CsTeam.CounterTerrorist, "HalfBuyPrimary", CTHalfBuy, ctPrefs);
        var ctSec  = BuildWeaponMenu("CT Secondaire",  player, CsTeam.CounterTerrorist, "Secondary",       CTSecondaries, ctPrefs);
        ctMenu.AddSubmenu("Full-Achat", ctFull);
        ctMenu.AddSubmenu("Semi-Achat", ctHalf);
        ctMenu.AddSubmenu("Secondaire",  ctSec);

        root.AddSubmenu("Terroriste (T)",        tMenu);
        root.AddSubmenu("Anti-Terroriste (CT)",  ctMenu);

        // Wire back buttons now that parent relationships are set
        tMenu.AddBack();  ctMenu.AddBack();
        tFull.AddBack();  tHalf.AddBack();  tSec.AddBack();
        ctFull.AddBack(); ctHalf.AddBack(); ctSec.AddBack();

        root.Open(player);
    }

    // Full-buy menu = toggle AWP (1ère ligne, vert si ON) + liste de rifles.
    // L'AWP n'est plus dans la liste des rifles : c'est un opt-in séparé qui place
    // le joueur dans la "queue AWP" — 1 carrier random par équipe est tiré chaque
    // round full-buy. Un opt-in non-carrier reçoit son rifle préféré normalement.
    private IMenu BuildFullBuyMenu(
        string title,
        CCSPlayerController player,
        CsTeam team,
        IEnumerable<(string Label, CsItem Item)> rifles,
        Dictionary<string, CsItem> prefs)
    {
        var menu = CS2UltimodPlugin.Menus.Create(title);

        // State local capturé par les closures : muté au clic, lu à chaque tick
        // par les fonctions de label/couleur → MAJ visuelle immédiate.
        var awpOn = prefs.ContainsKey("WantAwp");
        prefs.TryGetValue("FullBuyPrimary", out var current);

        menu.AddItem(
            labelFn: () => awpOn ? "AWP : ON ✔" : "AWP : OFF",
            onSelect: p =>
            {
                awpOn = !awpOn;
                var newState = awpOn;
                _ = Task.Run(async () =>
                {
                    try { await _allocator.SetWantAwpAsync(p.SteamID, team, newState); }
                    catch (Exception ex)
                    {
                        CS2UltimodPlugin.Log?.LogError(ex,
                            "[Allocator] SetWantAwpAsync failed steam={Steam} team={Team} on={On}",
                            p.SteamID, team, newState);
                    }
                });
                Chat.TellSuccess(p, newState
                    ? "Queue AWP : \x04ON\x01 — tu seras dans le tirage AWP en full-buy."
                    : "Queue AWP : \x04OFF\x01 — tu auras toujours ton rifle préféré.");
            },
            colorFn: () => awpOn ? "#5BFF5B" : null,
            keepOpen: true);

        foreach (var (label, item) in rifles)
        {
            var captured = item;
            var capturedLabel = label;
            menu.AddItem(
                labelFn: () => captured == current ? $"{capturedLabel} ✔" : capturedLabel,
                onSelect: p =>
                {
                    current = captured;
                    _ = Task.Run(async () =>
                    {
                        try { await _allocator.SetPrefAsync(p.SteamID, team, "FullBuyPrimary", captured); }
                        catch (Exception ex)
                        {
                            CS2UltimodPlugin.Log?.LogError(ex,
                                "[Allocator] SetPrefAsync failed steam={Steam} team={Team} weapon={Weapon}",
                                p.SteamID, team, captured);
                        }
                    });
                    Chat.TellSuccess(p, $"Arme enregistrée : {capturedLabel}");
                },
                keepOpen: true);
        }

        return menu;
    }

    private IMenu BuildWeaponMenu(
        string title,
        CCSPlayerController player,
        CsTeam team,
        string allocType,
        IEnumerable<(string Label, CsItem Item)> weapons,
        Dictionary<string, CsItem> prefs)
    {
        prefs.TryGetValue(allocType, out var current);
        var menu = CS2UltimodPlugin.Menus.Create(title);
        foreach (var (label, item) in weapons)
        {
            var captured = item;
            var capturedLabel = label;
            menu.AddItem(
                labelFn: () => captured == current ? $"{capturedLabel} ✔" : capturedLabel,
                onSelect: p =>
                {
                    current = captured;
                    _ = Task.Run(async () =>
                    {
                        try { await _allocator.SetPrefAsync(p.SteamID, team, allocType, captured); }
                        catch (Exception ex)
                        {
                            CS2UltimodPlugin.Log?.LogError(ex,
                                "[Allocator] SetPrefAsync failed steam={Steam} team={Team} alloc={Alloc} weapon={Weapon}",
                                p.SteamID, team, allocType, captured);
                        }
                    });
                    Chat.TellSuccess(p, $"Arme enregistrée : {capturedLabel}");
                },
                keepOpen: true);
        }
        return menu;
    }

    // ── Weapon lists ─────────────────────────────────────────────────────────

    private static readonly (string, CsItem)[] TFullBuy =
    [
        ("AK-47", CsItem.AK47), ("SG 553", CsItem.SG553),
        ("Galil AR", CsItem.GalilAR),
    ];

    private static readonly (string, CsItem)[] THalfBuy =
    [
        ("MAC-10", CsItem.Mac10), ("MP5-SD", CsItem.MP5),
        ("UMP-45", CsItem.UMP45), ("P90", CsItem.P90),
        ("Nova", CsItem.Nova), ("Sawed-Off", CsItem.SawedOff), ("XM1014", CsItem.XM1014),
        ("SSG 08 (Scout)", CsItem.SSG08),
    ];

    private static readonly (string, CsItem)[] CTFullBuy =
    [
        ("M4A4", CsItem.M4A4), ("M4A1-S", CsItem.M4A1S),
        ("AUG", CsItem.AUG), ("FAMAS", CsItem.Famas),
    ];

    private static readonly (string, CsItem)[] CTHalfBuy =
    [
        ("MP9", CsItem.MP9), ("MP5-SD", CsItem.MP5),
        ("UMP-45", CsItem.UMP45), ("P90", CsItem.P90),
        ("Nova", CsItem.Nova), ("MAG-7", CsItem.MAG7), ("XM1014", CsItem.XM1014),
        ("SSG 08 (Scout)", CsItem.SSG08),
    ];

    private static readonly (string, CsItem)[] TSecondaries =
    [
        ("Desert Eagle", CsItem.Deagle), ("Glock", CsItem.Glock),
        ("P250", CsItem.P250), ("Tec-9", CsItem.Tec9), ("CZ75", CsItem.CZ),
    ];

    private static readonly (string, CsItem)[] CTSecondaries =
    [
        ("Desert Eagle", CsItem.Deagle), ("USP-S", CsItem.USPS), ("P2000", CsItem.P2000),
        ("P250", CsItem.P250), ("Five-SeveN", CsItem.FiveSeven), ("CZ75", CsItem.CZ),
    ];
}
