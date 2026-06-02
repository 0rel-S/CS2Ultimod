using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Features.Admin;

/// <summary>
/// Builds the main !admin menu and all submenus using IMenuFramework.
/// </summary>
internal static class AdminMenu
{
    public static void Open(CCSPlayerController caller)
    {
        var menu = BuildMainMenu(caller);
        menu.Open(caller);
    }

    private static IMenu BuildMainMenu(CCSPlayerController caller)
    {
        var main = CS2UltimodPlugin.Menus.Create("Panneau Admin");

        main.AddSubmenu("Modération →", BuildModerationMenu(caller));
        main.AddSubmenu("Punitions →", BuildPunitionsMenu(caller));
        main.AddSubmenu("Serveur →", BuildServerMenu(caller));
        main.AddSubmenu("Admins →", BuildAdminsMenu(caller));

        return main;
    }

    // ── Modération ────────────────────────────────────────────────────────────

    private static IMenu BuildModerationMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Modération");

        menu.AddItem("Kick joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Kick — Choisir un joueur",
                (admin, target) =>
                {
                    AdminCommands.Kick(admin, [target.PlayerName]);
                });
            picker.Open(caller);
        });

        menu.AddItem("Ban joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Ban — Choisir un joueur",
                (admin, target) =>
                {
                    // Default: ban permanent, no reason from menu
                    AdminCommands.Ban(admin, [target.PlayerName, "0", "Banni via menu admin"]);
                });
            picker.Open(caller);
        });

        menu.AddItem("Gag / Mute", _ => BuildGagMuteMenu(caller).Open(caller));

        menu.AddItem("Unban (SteamID)", _ =>
        {
            // Can't enter text from menu — show instruction
            Chat.Tell(caller, "Utilisez la commande : !unban <steamid>");
        });

        menu.AddBack();
        return menu;
    }

    private static IMenu BuildGagMuteMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Gag / Mute");

        menu.AddItem("Gag joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Gag — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Gag(admin, [target.PlayerName, "0"]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddItem("Ungag joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Ungag — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Ungag(admin, [target.PlayerName]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddItem("Mute joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Mute — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Mute(admin, [target.PlayerName, "0"]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddItem("Unmute joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Unmute — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Unmute(admin, [target.PlayerName]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddBack();
        return menu;
    }

    // ── Punitions ─────────────────────────────────────────────────────────────

    private static IMenu BuildPunitionsMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Punitions");

        menu.AddItem("Slay", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Slay — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Slay(admin, [target.PlayerName]),
                filter: p => p.PawnIsAlive,
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddItem("Slap (5 dégâts)", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Slap — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Slap(admin, [target.PlayerName, "5"]),
                filter: p => p.PawnIsAlive,
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddItem("Freeze / Unfreeze", _ => BuildFreezeMenu(caller).Open(caller));

        menu.AddItem("God mode", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "God — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.God(admin, [target.PlayerName]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddBack();
        return menu;
    }

    private static IMenu BuildFreezeMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Freeze / Unfreeze");

        menu.AddItem("Geler un joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Freeze — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Freeze(admin, [target.PlayerName]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddItem("Dégeler un joueur", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Unfreeze — Choisir un joueur",
                (admin, target) =>
                    AdminCommands.Unfreeze(admin, [target.PlayerName]),
                keepOpen: true);
            picker.Open(caller);
        });

        menu.AddBack();
        return menu;
    }

    // ── Serveur ───────────────────────────────────────────────────────────────

    private static IMenu BuildServerMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Serveur");

        menu.AddItem("Changer la map", _ =>
        {
            Chat.Tell(caller, "Utilisez la commande : !map <nommap>");
        });

        menu.AddItem("Changer le mode", _ => BuildModeMenu(caller).Open(caller));

        menu.AddItem("Restart round", _ =>
        {
            var confirm = CS2UltimodPlugin.Menus.CreateConfirm(
                "Redémarrer le round ?",
                admin => AdminCommands.RestartRound(admin, []));
            confirm.Open(caller);
        });

        menu.AddItem("Restart game", _ =>
        {
            var confirm = CS2UltimodPlugin.Menus.CreateConfirm(
                "Redémarrer la partie ?",
                admin => AdminCommands.RestartGame(admin, []));
            confirm.Open(caller);
        });

        menu.AddBack();
        return menu;
    }

    private static IMenu BuildModeMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Changer le mode");

        foreach (var mode in Enum.GetValues<GameMode>())
        {
            var m = mode; // capture
            var isCurrent = CS2UltimodPlugin.ModeManager.Current == m;
            menu.AddItem($"{m}{(isCurrent ? " ✔" : "")}", admin =>
                AdminCommands.Mode(admin, [m.ToString().ToLower()]),
                enabled: !isCurrent);
        }

        menu.AddBack();
        return menu;
    }

    // ── Admins ────────────────────────────────────────────────────────────────

    private static IMenu BuildAdminsMenu(CCSPlayerController caller)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Gestion des admins");

        menu.AddItem("Ajouter admin", _ =>
        {
            var picker = CS2UltimodPlugin.Menus.CreatePlayerPicker(
                "Ajouter admin — Choisir un joueur",
                (admin, target) => BuildAddAdminLevelMenu(admin, target).Open(admin));
            picker.Open(caller);
        });

        menu.AddItem("Supprimer admin", _ => OpenDeleteAdminMenu(caller, menu));

        menu.AddItem("Recharger admins", _ =>
            AdminCommands.RelAdmin(caller, []));

        menu.AddBack();
        return menu;
    }

    // Liste les admins réellement enregistrés en base (pas les joueurs connectés) :
    // la lecture DB est async, donc on construit et ouvre le menu sur NextFrame.
    private static void OpenDeleteAdminMenu(CCSPlayerController caller, IMenu parent)
    {
        _ = Task.Run(async () =>
        {
            var admins = await CS2UltimodPlugin.Permissions.GetAllAdminsAsync();
            Server.NextFrame(() =>
            {
                if (admins.Count == 0)
                {
                    Chat.Tell(caller, "Aucun admin enregistré.");
                    return;
                }
                var del = CS2UltimodPlugin.Menus.Create("Supprimer admin");
                foreach (var a in admins)
                {
                    var sid = a.SteamId;
                    var label = $"{ResolveAdminName(sid)} ({string.Join(", ", a.Flags)})";
                    del.AddItem(label, admin => AdminCommands.DelAdmin(admin, [sid.ToString()]));
                }
                del.AddBack();
                // Ouvert hors d'un tick : on rattache explicitement le parent.
                del.WithParent(parent).Open(caller);
            });
        });
    }

    // Nom du joueur s'il est connecté, sinon son SteamID (admin hors-ligne).
    private static string ResolveAdminName(ulong steamId)
    {
        var p = Utilities.GetPlayers().FirstOrDefault(x => x.IsValid && x.SteamID == steamId);
        return p?.PlayerName ?? steamId.ToString();
    }

    private static IMenu BuildAddAdminLevelMenu(CCSPlayerController caller, CCSPlayerController target)
    {
        var menu = CS2UltimodPlugin.Menus.Create($"Niveau pour {target.PlayerName}");
        var sid = target.SteamID.ToString();

        menu.AddItem("Root (super-admin)", admin =>
            AdminCommands.AddAdmin(admin, [sid, "root"]));
        menu.AddItem("Mod (kick/ban/chat/slay)", admin =>
            AdminCommands.AddAdmin(admin, [sid, "mod"]));
        menu.AddItem("Basic (commandes safe)", admin =>
            AdminCommands.AddAdmin(admin, [sid, "basic"]));

        menu.AddBack();
        return menu;
    }
}
