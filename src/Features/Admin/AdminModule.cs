using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Utils;

// TODO: AdminModule.Register(Commands, EventBus, ...) must be called in CS2UltimodPlugin.Load()
// before DatabaseRegistry.RunMigrationsAsync() so that admin_* tables are created on first run.

namespace CS2Ultimod.Features.Admin;

/// <summary>
/// Boot class for the Admin module.
/// Call <see cref="Register"/> once from CS2UltimodPlugin.Load().
/// </summary>
public static class AdminModule
{
    public static void Register(
        ICommandRegistry commands,
        IModeAwareEventBus eventBus,
        BasePlugin plugin)
    {
        // 1. DB migration
        DatabaseRegistry.Register(new AdminMigration());

        // 2. Subscribe to RoundStart to refresh gag/mute state (all modes)
        eventBus.Subscribe<RoundStartEvent>(_ =>
            AdminCommands.RefreshCommState(CS2UltimodPlugin.Database));

        // 3. Hook say/say_team for gag enforcement
        plugin.AddCommandListener("say", OnSay);
        plugin.AddCommandListener("say_team", OnSay);

        // 4. Register all commands
        RegisterCommands(commands);
    }

    // ── Gag enforcement ───────────────────────────────────────────────────────

    private static HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;

        if (AdminCommands.GaggedPlayers.Contains(player.SteamID))
        {
            Chat.TellError(player, "Vous êtes bâillonné et ne pouvez pas parler.");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    // ── Command registration ──────────────────────────────────────────────────

    private static void RegisterCommands(ICommandRegistry commands)
    {
        // ── Modération ────────────────────────────────────────────────────────

        commands.Register(new ChatCommand(
            "kick", null, "@css/kick",
            "Expulse un ou plusieurs joueurs. Usage : !kick <cible> [raison]",
            AdminCommands.Kick));

        commands.Register(new ChatCommand(
            "ban", null, "@css/ban",
            "Bannit un joueur (0 = permanent). Usage : !ban <cible> <durée_min> [raison]",
            AdminCommands.Ban));

        commands.Register(new ChatCommand(
            "banip", null, "@css/ban",
            "Bannit une IP. Usage : !banip <ip> <durée_min> [raison]",
            AdminCommands.BanIp));

        commands.Register(new ChatCommand(
            "addban", null, "@css/ban",
            "Bannit un joueur hors-ligne. Usage : !addban <steamid> <durée_min> [raison]",
            AdminCommands.AddBan));

        commands.Register(new ChatCommand(
            "unban", null, "@css/ban",
            "Lève le ban d'un joueur. Usage : !unban <steamid>",
            AdminCommands.Unban));

        commands.Register(new ChatCommand(
            "gag", null, "@css/chat",
            "Bloque le chat d'un joueur. Usage : !gag <cible> <durée_min> [raison]",
            AdminCommands.Gag));

        commands.Register(new ChatCommand(
            "ungag", null, "@css/chat",
            "Restaure le chat d'un joueur. Usage : !ungag <cible>",
            AdminCommands.Ungag));

        commands.Register(new ChatCommand(
            "mute", null, "@css/chat",
            "Bloque la voix d'un joueur. Usage : !mute <cible> <durée_min> [raison]",
            AdminCommands.Mute));

        commands.Register(new ChatCommand(
            "unmute", null, "@css/chat",
            "Restaure la voix d'un joueur. Usage : !unmute <cible>",
            AdminCommands.Unmute));

        commands.Register(new ChatCommand(
            "silence", null, "@css/chat",
            "Bloque chat + voix d'un joueur. Usage : !silence <cible> <durée_min> [raison]",
            AdminCommands.Silence));

        commands.Register(new ChatCommand(
            "unsilence", null, "@css/chat",
            "Restaure chat + voix d'un joueur. Usage : !unsilence <cible>",
            AdminCommands.Unsilence));

        // ── Punitions ─────────────────────────────────────────────────────────

        commands.Register(new ChatCommand(
            "slay", null, "@css/slay",
            "Tue un ou plusieurs joueurs. Usage : !slay <cible>",
            AdminCommands.Slay));

        commands.Register(new ChatCommand(
            "slap", null, "@css/slay",
            "Inflige des dégâts à un joueur. Usage : !slap <cible> [dégâts]",
            AdminCommands.Slap));

        commands.Register(new ChatCommand(
            "freeze", null, "@css/slay",
            "Immobilise un joueur. Usage : !freeze <cible> [durée_sec]",
            AdminCommands.Freeze));

        commands.Register(new ChatCommand(
            "unfreeze", null, "@css/slay",
            "Libère un joueur gelé. Usage : !unfreeze <cible>",
            AdminCommands.Unfreeze));

        commands.Register(new ChatCommand(
            "noclip", null, "@css/root",
            "Active/désactive le noclip. Usage : !noclip <cible>",
            AdminCommands.Noclip));

        commands.Register(new ChatCommand(
            "respawn", null, "@css/slay",
            "Réanime un joueur mort. Usage : !respawn <cible>",
            AdminCommands.Respawn));

        commands.Register(new ChatCommand(
            "god", null, "@css/root",
            "Active/désactive le mode dieu. Usage : !god <cible>",
            AdminCommands.God));

        // ── Joueurs ───────────────────────────────────────────────────────────

        commands.Register(new ChatCommand(
            "team", null, "@css/kick",
            "Déplace un joueur dans une équipe. Usage : !team <cible> <T|CT|SPEC>",
            AdminCommands.Team));

        commands.Register(new ChatCommand(
            "swap", null, "@css/kick",
            "Inverse l'équipe d'un joueur. Usage : !swap <cible>",
            AdminCommands.Swap));

        commands.Register(new ChatCommand(
            "rename", null, "@css/generic",
            "Renomme un joueur. Usage : !rename <cible> <nouveau_nom>",
            AdminCommands.Rename));

        commands.Register(new ChatCommand(
            "who", null, "@css/generic",
            "Affiche infos d'un joueur. Usage : !who <cible>",
            AdminCommands.Who));

        commands.Register(new ChatCommand(
            "players", null, "@css/generic",
            "Liste tous les joueurs connectés.",
            AdminCommands.Players));

        commands.Register(new ChatCommand(
            "disconnected", null, "@css/generic",
            "Affiche les 10 derniers joueurs déconnectés.",
            AdminCommands.Disconnected));

        // ── Serveur ───────────────────────────────────────────────────────────

        commands.Register(new ChatCommand(
            "map", ["changemap"], "@css/changemap",
            "Change la map. Usage : !map <nommap>",
            AdminCommands.Map));

        commands.Register(new ChatCommand(
            "wsmap", null, "@css/changemap",
            "Charge une map Workshop. Usage : !wsmap <workshopid>",
            AdminCommands.WsMap));

        commands.Register(new ChatCommand(
            "rcon", null, "@css/rcon",
            "Exécute une commande serveur. Usage : !rcon <commande>",
            AdminCommands.Rcon));

        commands.Register(new ChatCommand(
            "cvar", null, "@css/cvar",
            "Lit ou écrit une convar. Usage : !cvar <nom> [valeur]",
            AdminCommands.Cvar));

        commands.Register(new ChatCommand(
            "rr", null, "@css/generic",
            "Redémarre le round.",
            AdminCommands.RestartRound));

        commands.Register(new ChatCommand(
            "rg", null, "@css/root",
            "Redémarre la partie.",
            AdminCommands.RestartGame));

        commands.Register(new ChatCommand(
            "extend", null, "@css/changemap",
            "Prolonge le temps de la map. Usage : !extend <minutes>",
            AdminCommands.Extend));

        commands.Register(new ChatCommand(
            "hsay", null, "@css/chat",
            "Envoie un message HUD à tous. Usage : !hsay <message>",
            AdminCommands.Hsay));

        commands.Register(new ChatCommand(
            "say", null, "@css/chat",
            "Envoie un message en tant qu'[ADMIN]. Usage : !say <message>",
            AdminCommands.Say));

        commands.Register(new ChatCommand(
            "psay", null, "@css/chat",
            "Envoie un message privé à un joueur. Usage : !psay <cible> <message>",
            AdminCommands.Psay));

        commands.Register(new ChatCommand(
            "csay", null, "@css/chat",
            "Envoie un message [ADMIN] à tous. Usage : !csay <message>",
            AdminCommands.Csay));

        commands.Register(new ChatCommand(
            "stats", null, null,
            "Affiche les statistiques du serveur.",
            AdminCommands.Stats));

        // ── Admin management ──────────────────────────────────────────────────

        commands.Register(new ChatCommand(
            "addadmin", null, "@css/root",
            "Ajoute un admin. Usage : !addadmin <steamid> <root|mod|basic> [durée_min]",
            AdminCommands.AddAdmin));

        commands.Register(new ChatCommand(
            "deladmin", null, "@css/root",
            "Supprime un admin. Usage : !deladmin <steamid>",
            AdminCommands.DelAdmin));

        commands.Register(new ChatCommand(
            "reladmin", null, "@css/root",
            "Recharge les admins depuis la DB.",
            AdminCommands.RelAdmin));

        // ── Menu / Utilitaires ────────────────────────────────────────────────

        commands.Register(new ChatCommand(
            "admin", null, "@css/generic",
            "Ouvre le panneau admin.",
            (caller, _) => AdminMenu.Open(caller)));

        commands.Register(new ChatCommand(
            "help", null, null,
            "Affiche les commandes disponibles.",
            AdminCommands.Help));

        commands.Register(new ChatCommand(
            "mode", null, "@cs2ultimod/mode",
            "Change le mode de jeu. Usage : !mode <retake|execute|mixte|stuff|pickup>",
            AdminCommands.Mode));

        // votemode est géré par VoteModule
    }
}

// ── DB Migration ──────────────────────────────────────────────────────────────

internal sealed class AdminMigration : IMigration
{
    public string Id => "admin_v1_init";
    public int Version => 100;

    public async Task UpAsync(IDatabase db)
    {
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS admin_admins (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id   TEXT NOT NULL,
                flag       TEXT NOT NULL,
                expires_at TEXT
            )
            """);

        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS admin_bans (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id   TEXT NOT NULL,
                ip         TEXT,
                admin_id   TEXT NOT NULL,
                reason     TEXT NOT NULL DEFAULT '',
                duration   INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                expires_at TEXT,
                unbanned   INTEGER NOT NULL DEFAULT 0,
                unban_by   TEXT
            )
            """);

        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS admin_comms (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id   TEXT NOT NULL,
                admin_id   TEXT NOT NULL,
                type       TEXT NOT NULL,
                reason     TEXT NOT NULL DEFAULT '',
                duration   INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                expires_at TEXT,
                removed    INTEGER NOT NULL DEFAULT 0
            )
            """);

        // Disconnected log table (used by !disconnected command)
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS admin_disconnected (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id         TEXT NOT NULL,
                name             TEXT NOT NULL DEFAULT '',
                disconnected_at  TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);
    }
}
