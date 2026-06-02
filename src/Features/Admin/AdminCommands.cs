using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Features.Admin.Models;

namespace CS2Ultimod.Features.Admin;

/// <summary>
/// All admin command implementations. Each method is called from AdminModule.
/// </summary>
internal static class AdminCommands
{
    // ── Gag/Mute state (in-memory, refreshed on RoundStart) ─────────────────

    internal static readonly HashSet<ulong> GaggedPlayers = [];
    internal static readonly HashSet<ulong> MutedPlayers = [];

    // SteamID du propriétaire du serveur — ne peut jamais être renommé via !rename.
    private const ulong OwnerSteamId = 76561198007698872;

    internal static void RefreshCommState(IDatabase db)
    {
        _ = Task.Run(async () =>
        {
            var rows = await db.QueryAsync<CommRecord>(
                "SELECT steam_id, type FROM admin_comms WHERE removed = 0 AND (expires_at IS NULL OR expires_at > datetime('now'))");

            var gagged = new HashSet<ulong>();
            var muted = new HashSet<ulong>();

            foreach (var row in rows)
            {
                if (!ulong.TryParse(row.SteamId, out var sid)) continue;
                if (row.Type == "gag" || row.Type == "silence") gagged.Add(sid);
                if (row.Type == "mute" || row.Type == "silence") muted.Add(sid);
            }

            // Switch on main thread
            Server.NextFrame(() =>
            {
                GaggedPlayers.Clear();
                foreach (var s in gagged) GaggedPlayers.Add(s);

                MutedPlayers.Clear();
                foreach (var s in muted) MutedPlayers.Add(s);

                // Apply mute flags to currently connected players
                foreach (var p in PlayerExt.AllConnected())
                {
                    if (p.VoiceFlags.HasFlag(VoiceFlags.Muted) != MutedPlayers.Contains(p.SteamID))
                    {
                        p.VoiceFlags = MutedPlayers.Contains(p.SteamID)
                            ? VoiceFlags.Muted
                            : VoiceFlags.Normal;
                    }
                }
            });
        });
    }

    // ── Helper: admin display name ───────────────────────────────────────────

    private static string AdminName(CCSPlayerController admin)
        => admin.IsValid ? admin.PlayerName : "Console";

    private static string AdminSteamId(CCSPlayerController admin)
        => admin.IsValid ? admin.SteamID.ToString() : "CONSOLE";

    // ── Modération ───────────────────────────────────────────────────────────

    public static void Kick(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !kick <cible> [raison]"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        var reason = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "Aucune raison";

        foreach (var t in targets)
        {
            Chat.Broadcast($"{AdminName(caller)} a expulsé {t.PlayerName} ({reason})");
            t.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        }
    }

    public static void Ban(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2) { Chat.TellError(caller, "Usage : !ban <cible> <durée_min> [raison]"); return; }
        if (!int.TryParse(args[1], out var duration)) { Chat.TellError(caller, "Durée invalide."); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "";

        foreach (var t in targets)
        {
            var steamId = t.SteamID.ToString();
            var ip = t.IpAddress ?? "";
            _ = BanPlayerAsync(caller, steamId, ip, duration, reason);
            Chat.Broadcast($"{AdminName(caller)} a banni {t.PlayerName} ({(duration == 0 ? "permanent" : $"{duration}min")}) : {reason}");
            t.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        }
    }

    public static void BanIp(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2) { Chat.TellError(caller, "Usage : !banip <ip> <durée_min> [raison]"); return; }
        if (!int.TryParse(args[1], out var duration)) { Chat.TellError(caller, "Durée invalide."); return; }

        var ip = args[0];
        var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "";

        _ = BanIpAsync(caller, ip, duration, reason);
        Chat.Broadcast($"{AdminName(caller)} a banni l'IP {ip} ({(duration == 0 ? "permanent" : $"{duration}min")}) : {reason}");

        // Kick any connected player with that IP
        foreach (var p in PlayerExt.AllConnected())
        {
            if (p.IpAddress?.Split(':')[0] == ip.Split(':')[0])
                p.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        }
    }

    public static void AddBan(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2) { Chat.TellError(caller, "Usage : !addban <steamid> <durée_min> [raison]"); return; }
        if (!int.TryParse(args[1], out var duration)) { Chat.TellError(caller, "Durée invalide."); return; }

        var steamId = args[0];
        var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "";

        _ = BanPlayerAsync(caller, steamId, null, duration, reason);
        Chat.TellSuccess(caller, $"Ban ajouté pour {steamId} ({(duration == 0 ? "permanent" : $"{duration}min")}).");
    }

    public static void Unban(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !unban <steamid>"); return; }

        var steamId = args[0];
        _ = UnbanAsync(caller, steamId);
    }

    public static void Gag(CCSPlayerController caller, string[] args)
        => ApplyComm(caller, args, "gag", "bâillonné");

    public static void Ungag(CCSPlayerController caller, string[] args)
        => RemoveComm(caller, args, "gag", "débâillonné");

    public static void Mute(CCSPlayerController caller, string[] args)
        => ApplyComm(caller, args, "mute", "muet");

    public static void Unmute(CCSPlayerController caller, string[] args)
        => RemoveComm(caller, args, "mute", "démuet");

    public static void Silence(CCSPlayerController caller, string[] args)
        => ApplyComm(caller, args, "silence", "réduit au silence");

    public static void Unsilence(CCSPlayerController caller, string[] args)
        => RemoveComm(caller, args, "silence", "libéré du silence");

    // ── Punitions ────────────────────────────────────────────────────────────

    public static void Slay(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !slay <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            if (!t.PawnIsAlive) continue;
            t.PlayerPawn.Value?.CommitSuicide(false, true);
            Chat.Broadcast($"{AdminName(caller)} a tué {t.PlayerName}");
        }
    }

    public static void Slap(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !slap <cible> [dégâts]"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        int.TryParse(args.Length > 1 ? args[1] : "0", out var damage);

        foreach (var t in targets)
        {
            if (!t.PawnIsAlive) continue;
            var pawn = t.PlayerPawn.Value;
            if (pawn == null) continue;

            if (damage > 0)
            {
                pawn.Health -= damage;
                if (pawn.Health <= 0)
                    pawn.CommitSuicide(false, true);
                else
                    Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
            }
            Chat.TellSuccess(caller, $"Slap appliqué à {t.PlayerName} ({damage} dégâts).");
        }
    }

    public static void Freeze(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !freeze <cible> [durée_sec]"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        int.TryParse(args.Length > 1 ? args[1] : "0", out var duration);

        foreach (var t in targets)
        {
            var pawn = t.PlayerPawn.Value;
            if (pawn == null) continue;
            pawn.MoveType = MoveType_t.MOVETYPE_NONE;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
            Chat.TellSuccess(caller, $"{t.PlayerName} est gelé.");

            if (duration > 0)
            {
                var slot = t.Slot;
                _ = Task.Delay(TimeSpan.FromSeconds(duration)).ContinueWith(_ =>
                    Server.NextFrame(() =>
                    {
                        var p = Utilities.GetPlayerFromSlot(slot);
                        if (p is { IsValid: true, PawnIsAlive: true } && p.PlayerPawn.Value != null)
                        {
                            p.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_WALK;
                            Utilities.SetStateChanged(p.PlayerPawn.Value, "CBaseEntity", "m_MoveType");
                        }
                    }));
            }
        }
    }

    public static void Unfreeze(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !unfreeze <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            var pawn = t.PlayerPawn.Value;
            if (pawn == null) continue;
            pawn.MoveType = MoveType_t.MOVETYPE_WALK;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
            Chat.TellSuccess(caller, $"{t.PlayerName} est dégelé.");
        }
    }

    public static void Noclip(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !noclip <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            var pawn = t.PlayerPawn.Value;
            if (pawn == null) continue;
            bool wasNoclip = pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP;
            pawn.MoveType = wasNoclip ? MoveType_t.MOVETYPE_WALK : MoveType_t.MOVETYPE_NOCLIP;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
            Chat.TellSuccess(caller, $"Noclip {(wasNoclip ? "désactivé" : "activé")} pour {t.PlayerName}.");
        }
    }

    public static void Respawn(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !respawn <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            if (t.PawnIsAlive) { Chat.TellError(caller, $"{t.PlayerName} est déjà en vie."); continue; }
            t.Respawn();
            Chat.TellSuccess(caller, $"{t.PlayerName} a été réanimé.");
        }
    }

    public static void God(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !god <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            var pawn = t.PlayerPawn.Value;
            if (pawn == null) continue;
            bool wasGod = pawn.TakesDamage == false;
            pawn.TakesDamage = wasGod;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_bTakesDamage");
            Chat.TellSuccess(caller, $"Mode dieu {(wasGod ? "désactivé" : "activé")} pour {t.PlayerName}.");
        }
    }

    // ── Joueurs ──────────────────────────────────────────────────────────────

    public static void Team(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2) { Chat.TellError(caller, "Usage : !team <cible> <T|CT|SPEC>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        var teamStr = args[1].ToUpper();
        CsTeam team = teamStr switch
        {
            "T" => CsTeam.Terrorist,
            "CT" => CsTeam.CounterTerrorist,
            "SPEC" or "SPECTATOR" => CsTeam.Spectator,
            _ => CsTeam.None
        };

        if (team == CsTeam.None) { Chat.TellError(caller, "Équipe invalide. Valeurs : T, CT, SPEC"); return; }

        foreach (var t in targets)
        {
            t.SwitchTeam(team);
            Chat.TellSuccess(caller, $"{t.PlayerName} déplacé vers {teamStr}.");
        }
    }

    public static void Swap(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !swap <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            var newTeam = t.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist
                : t.Team == CsTeam.CounterTerrorist ? CsTeam.Terrorist
                : CsTeam.Spectator;
            t.SwitchTeam(newTeam);
            Chat.TellSuccess(caller, $"{t.PlayerName} changé d'équipe.");
        }
    }

    public static void Rename(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2) { Chat.TellError(caller, "Usage : !rename <cible> <nouveau_nom>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        // !rename ne vise qu'un seul joueur. Un nom partiel peut matcher plusieurs
        // personnes (pseudos identiques notamment) : on refuse plutôt que de tous
        // les renommer d'un coup.
        if (targets.Count > 1)
        {
            Chat.TellError(caller, $"{targets.Count} joueurs correspondent à « {args[0]} ». Précisez une cible unique.");
            return;
        }

        var target = targets[0];

        if (target.SteamID == OwnerSteamId)
        {
            Chat.TellError(caller, "Ce joueur ne peut pas être renommé.");
            return;
        }

        var newName = string.Join(" ", args.Skip(1));
        var oldName = target.PlayerName;
        target.PlayerName = newName;
        Utilities.SetStateChanged(target, "CBasePlayerController", "m_iszPlayerName");
        Chat.Broadcast($"{AdminName(caller)} a renommé {oldName} en {newName}.");
    }

    public static void Who(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !who <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            var teamName = t.Team switch
            {
                CsTeam.Terrorist => "Terroriste",
                CsTeam.CounterTerrorist => "Anti-terroriste",
                CsTeam.Spectator => "Spectateur",
                _ => "Aucune"
            };
            Chat.Tell(caller, $"[{t.PlayerName}] SteamID: {t.SteamID} | IP: {t.IpAddress ?? "N/A"} | Équipe: {teamName} | Ping: {t.Ping}ms");
        }
    }

    public static void Players(CCSPlayerController caller, string[] args)
    {
        var connected = PlayerExt.AllConnected().ToList();
        Chat.Tell(caller, $"Joueurs connectés ({connected.Count}) :");
        foreach (var p in connected)
        {
            var teamTag = p.Team switch
            {
                CsTeam.Terrorist => "[T]",
                CsTeam.CounterTerrorist => "[CT]",
                CsTeam.Spectator => "[SPEC]",
                _ => "[?]"
            };
            Chat.Tell(caller, $"  {teamTag} {p.PlayerName} | {p.SteamID} | {p.IpAddress ?? "N/A"} | {p.Ping}ms");
        }
    }

    public static void Disconnected(CCSPlayerController caller, string[] args)
    {
        _ = Task.Run(async () =>
        {
            var rows = await CS2UltimodPlugin.Database.QueryAsync<DisconnectedRow>(
                "SELECT steam_id, name, disconnected_at FROM admin_disconnected ORDER BY disconnected_at DESC LIMIT 10");
            Server.NextFrame(() =>
            {
                Chat.Tell(caller, "Derniers joueurs déconnectés :");
                if (rows.Count == 0) { Chat.Tell(caller, "  Aucun."); return; }
                foreach (var r in rows)
                    Chat.Tell(caller, $"  {r.name} | {r.steam_id} | {r.disconnected_at}");
            });
        });
    }

    // ── Serveur ──────────────────────────────────────────────────────────────

    public static void Map(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !map <nommap>"); return; }
        var input = args[0].ToLowerInvariant();
        var resolved = ResolveMapName(input);
        if (resolved == null)
        {
            Chat.TellError(caller, $"Aucune map trouvée pour '{input}'. Maps connues : {string.Join(", ", KnownMaps)}");
            return;
        }
        Chat.Broadcast($"{AdminName(caller)} change la map vers {resolved}...");
        Server.ExecuteCommand($"map {resolved}");
    }

    // Standard CS2 competitive maps + a few classics. Used to fuzzy-match "inf" → "de_inferno".
    private static readonly string[] KnownMaps =
    [
        "de_ancient", "de_anubis", "de_dust2", "de_inferno", "de_mirage", "de_nuke",
        "de_overpass", "de_train", "de_vertigo", "de_cache", "de_cbble", "de_thera",
        "cs_office", "cs_italy", "cs_agency",
    ];

    private static string? ResolveMapName(string input)
    {
        // Exact match wins
        if (KnownMaps.Contains(input)) return input;
        // Strip prefix (de_/cs_) for matching
        var bare = input.StartsWith("de_") || input.StartsWith("cs_") ? input.Substring(3) : input;
        // Prefix match on the bare name (e.g. "inf" → "de_inferno")
        var prefix = KnownMaps.Where(m => m.Substring(3).StartsWith(bare)).ToList();
        if (prefix.Count == 1) return prefix[0];
        if (prefix.Count > 1) return null; // ambiguous
        // Substring match as fallback (e.g. "fern" → "de_inferno")
        var contains = KnownMaps.Where(m => m.Substring(3).Contains(bare)).ToList();
        if (contains.Count == 1) return contains[0];
        // Last resort: pass the input through as-is (lets the user load custom maps not in the list)
        return input;
    }

    public static void WsMap(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !wsmap <workshopid>"); return; }
        var workshopId = args[0];
        Chat.Broadcast($"{AdminName(caller)} charge la map Workshop {workshopId}...");
        Server.ExecuteCommand($"host_workshop_map {workshopId}");
    }

    public static void Rcon(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !rcon <commande>"); return; }
        var cmd = string.Join(" ", args);
        Server.ExecuteCommand(cmd);
        Chat.TellSuccess(caller, $"Commande exécutée : {cmd}");
    }

    public static void Cvar(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !cvar <nom> [valeur]"); return; }

        var name = args[0];
        var convar = ConVar.Find(name);
        if (convar == null) { Chat.TellError(caller, $"Convar '{name}' introuvable."); return; }

        if (args.Length == 1)
        {
            Chat.Tell(caller, $"{name} = {convar.StringValue}");
        }
        else
        {
            var value = string.Join(" ", args.Skip(1));
            Server.ExecuteCommand($"{name} {value}");
            Chat.TellSuccess(caller, $"{name} défini à {value}.");
        }
    }

    public static void RestartRound(CCSPlayerController caller, string[] args)
    {
        Chat.Broadcast($"{AdminName(caller)} a redémarré le round.");
        Server.ExecuteCommand("mp_restartgame 1");
    }

    public static void RestartGame(CCSPlayerController caller, string[] args)
    {
        Chat.Broadcast($"{AdminName(caller)} a redémarré la partie.");
        Server.ExecuteCommand("mp_restartgame 5");
    }

    public static void Extend(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !extend <minutes>"); return; }
        if (!int.TryParse(args[0], out var minutes)) { Chat.TellError(caller, "Durée invalide."); return; }

        var convar = ConVar.Find("mp_timelimit");
        if (convar != null && float.TryParse(convar.StringValue, out var current))
        {
            var newTime = (int)(current + minutes);
            Server.ExecuteCommand($"mp_timelimit {newTime}");
            Chat.Broadcast($"{AdminName(caller)} a prolongé la map de {minutes} minute(s).");
        }
        else
        {
            Chat.TellError(caller, "Impossible de lire mp_timelimit.");
        }
    }

    public static void Hsay(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !hsay <message>"); return; }
        var msg = string.Join(" ", args);
        Chat.HudCenterAll(msg, 5f);
    }

    public static void Say(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !say <message>"); return; }
        var msg = string.Join(" ", args);
        Chat.Broadcast($"\x0E[ADMIN] {AdminName(caller)}\x01 : {msg}");
    }

    public static void Psay(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2) { Chat.TellError(caller, "Usage : !psay <cible> <message>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        var msg = string.Join(" ", args.Skip(1));

        foreach (var t in targets)
        {
            Chat.Tell(t, $"\x0E[Message privé de {AdminName(caller)}]\x01 : {msg}");
            Chat.TellSuccess(caller, $"Message envoyé à {t.PlayerName}.");
        }
    }

    public static void Csay(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !csay <message>"); return; }
        var msg = string.Join(" ", args);
        Chat.Broadcast($"\x0E[ADMIN]\x01 {msg}");
    }

    public static void Stats(CCSPlayerController caller, string[] args)
    {
        var connected = Utilities.GetPlayers().Count(p => p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected);
        var map = Server.MapName;
        var mode = CS2UltimodPlugin.ModeManager.Current;
        var uptime = TimeSpan.FromSeconds(Server.CurrentTime);
        Chat.Tell(caller, $"Serveur : {connected} joueur(s) | Map: {map} | Mode: {mode} | Uptime: {uptime:hh\\:mm\\:ss}");
    }

    // ── Admin management ─────────────────────────────────────────────────────

    // Presets pour !addadmin — saisir un preset au lieu d'une liste de flags.
    // "root"  = super-admin (tout)
    // "mod"   = modération courante (kick/ban/chat/slay)
    // "basic" = commandes safe (who/players/rename/rr/disconnected)
    private static readonly Dictionary<string, string[]> AdminPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["root"]  = ["@css/root"],
        ["mod"]   = ["@css/kick", "@css/ban", "@css/chat", "@css/slay"],
        ["basic"] = ["@css/generic"],
    };

    public static void AddAdmin(CCSPlayerController caller, string[] args)
    {
        if (args.Length < 2)
        {
            Chat.TellError(caller, "Usage : !addadmin <steamid> <preset|flags> [durée_min]");
            Chat.Tell(caller, "Presets : \x04root\x01, \x04mod\x01, \x04basic\x01 — ou flags séparés par virgule (ex: @css/kick,@css/ban)");
            return;
        }

        var steamIdStr = args[0];
        if (!ulong.TryParse(steamIdStr, out var steamId)) { Chat.TellError(caller, "SteamID invalide."); return; }

        // Si l'arg correspond à un preset, on le déroule ; sinon on traite comme liste de flags.
        var flags = AdminPresets.TryGetValue(args[1], out var presetFlags)
            ? presetFlags
            : args[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        DateTimeOffset? expiresAt = null;

        if (args.Length >= 3 && int.TryParse(args[2], out var durationMin) && durationMin > 0)
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(durationMin);

        _ = Task.Run(async () =>
        {
            await CS2UltimodPlugin.Permissions.SetFlagsAsync(steamId, flags, expiresAt);
            Server.NextFrame(() =>
                Chat.TellSuccess(caller, $"Admin {steamIdStr} ajouté avec les flags : {string.Join(", ", flags)}."));
        });
    }

    public static void DelAdmin(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0) { Chat.TellError(caller, "Usage : !deladmin <steamid>"); return; }

        var steamIdStr = args[0];
        if (!ulong.TryParse(steamIdStr, out var steamId)) { Chat.TellError(caller, "SteamID invalide."); return; }

        _ = Task.Run(async () =>
        {
            await CS2UltimodPlugin.Permissions.RemoveAsync(steamId);
            Server.NextFrame(() =>
                Chat.TellSuccess(caller, $"Admin {steamIdStr} supprimé."));
        });
    }

    public static void RelAdmin(CCSPlayerController caller, string[] args)
    {
        _ = Task.Run(async () =>
        {
            await CS2UltimodPlugin.Permissions.ReloadAsync();
            Server.NextFrame(() =>
                Chat.TellSuccess(caller, "Admins rechargés depuis la base de données."));
        });
    }

    // ── Mode ─────────────────────────────────────────────────────────────────

    public static void Mode(CCSPlayerController caller, string[] args)
    {
        if (args.Length == 0)
        {
            Chat.TellError(caller, "Usage : !mode <retake|execute|mixte|stuff|pickup|arena>");
            Chat.Tell(caller, $"Mode actuel : {CS2UltimodPlugin.ModeManager.Current}");
            return;
        }

        var modeStr = args[0].ToLower();
        GameMode? mode = modeStr switch
        {
            "retake" => GameMode.Retake,
            "execute" => GameMode.Execute,
            "mixte" => GameMode.Mixte,
            "stuff" => GameMode.Stuff,
            "pickup" => GameMode.Pickup,
            "arena" => GameMode.Arena,
            _ => null
        };

        if (mode == null)
        {
            Chat.TellError(caller, $"Mode inconnu : {modeStr}. Valeurs : retake, execute, mixte, stuff, pickup, arena");
            return;
        }

        Chat.Broadcast($"{AdminName(caller)} change le mode vers {mode}...");
        _ = CS2UltimodPlugin.ModeManager.SwitchToAsync(mode.Value, reloadMap: false, reason: $"admin:{AdminName(caller)}");
    }

    public static void VoteMode(CCSPlayerController caller, string[] args)
        => CS2Ultimod.Features.Votes.VoteModule.TriggerVoteMode(caller, args);

    // ── Help ─────────────────────────────────────────────────────────────────

    public static void Help(CCSPlayerController caller, string[] args)
    {
        var registry = (CS2Ultimod.Core.Utils.CommandRegistry)CS2UltimodPlugin.Commands;
        var available = registry.All.Values
            .Distinct()
            .Where(cmd => cmd.RequiredFlag == null || CS2UltimodPlugin.Permissions.HasFlag(caller, cmd.RequiredFlag))
            .OrderBy(cmd => cmd.Name)
            .ToList();

        Chat.Tell(caller, $"Commandes disponibles ({available.Count}) :");
        foreach (var cmd in available)
            Chat.Tell(caller, $"  !{cmd.Name} — {cmd.Usage}");
    }

    // ── Internal async helpers ───────────────────────────────────────────────

    private static async Task BanPlayerAsync(CCSPlayerController admin, string steamId, string? ip, int duration, string reason)
    {
        var expiresAt = duration > 0
            ? DateTime.UtcNow.AddMinutes(duration).ToString("yyyy-MM-dd HH:mm:ss")
            : null;

        await CS2UltimodPlugin.Database.ExecuteAsync(
            """
            INSERT INTO admin_bans (steam_id, ip, admin_id, reason, duration, expires_at)
            VALUES (@SteamId, @Ip, @AdminId, @Reason, @Duration, @ExpiresAt)
            """,
            new
            {
                SteamId = steamId,
                Ip = ip,
                AdminId = AdminSteamId(admin),
                Reason = reason,
                Duration = duration,
                ExpiresAt = expiresAt
            });
    }

    private static async Task BanIpAsync(CCSPlayerController admin, string ip, int duration, string reason)
    {
        var expiresAt = duration > 0
            ? DateTime.UtcNow.AddMinutes(duration).ToString("yyyy-MM-dd HH:mm:ss")
            : null;

        await CS2UltimodPlugin.Database.ExecuteAsync(
            """
            INSERT INTO admin_bans (steam_id, ip, admin_id, reason, duration, expires_at)
            VALUES ('', @Ip, @AdminId, @Reason, @Duration, @ExpiresAt)
            """,
            new
            {
                Ip = ip,
                AdminId = AdminSteamId(admin),
                Reason = reason,
                Duration = duration,
                ExpiresAt = expiresAt
            });
    }

    private static async Task UnbanAsync(CCSPlayerController admin, string steamId)
    {
        var rows = await CS2UltimodPlugin.Database.ExecuteAsync(
            "UPDATE admin_bans SET unbanned = 1, unban_by = @AdminId WHERE steam_id = @SteamId AND unbanned = 0",
            new { SteamId = steamId, AdminId = AdminSteamId(admin) });

        Server.NextFrame(() =>
        {
            if (rows > 0)
                Chat.TellSuccess(admin, $"Ban levé pour {steamId}.");
            else
                Chat.TellError(admin, $"Aucun ban actif trouvé pour {steamId}.");
        });
    }

    private static void ApplyComm(CCSPlayerController caller, string[] args, string type, string pastTense)
    {
        if (args.Length < 2)
        {
            Chat.TellError(caller, $"Usage : !{type} <cible> <durée_min> [raison]");
            return;
        }
        if (!int.TryParse(args[1], out var duration)) { Chat.TellError(caller, "Durée invalide."); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "";

        foreach (var t in targets)
        {
            _ = SaveCommAsync(t, AdminSteamId(caller), type, reason, duration);

            if (type == "gag" || type == "silence") GaggedPlayers.Add(t.SteamID);
            if (type == "mute" || type == "silence")
            {
                MutedPlayers.Add(t.SteamID);
                t.VoiceFlags = VoiceFlags.Muted;
            }
            Chat.Broadcast($"{AdminName(caller)} a {pastTense} {t.PlayerName} ({(duration == 0 ? "permanent" : $"{duration}min")}) : {reason}");
        }
    }

    private static void RemoveComm(CCSPlayerController caller, string[] args, string type, string pastTense)
    {
        if (args.Length == 0) { Chat.TellError(caller, $"Usage : !un{type} <cible>"); return; }

        var targets = PlayerExt.Resolve(args[0], caller);
        if (targets.Count == 0) { Chat.TellError(caller, "Aucun joueur trouvé."); return; }

        foreach (var t in targets)
        {
            _ = CS2UltimodPlugin.Database.ExecuteAsync(
                "UPDATE admin_comms SET removed = 1 WHERE steam_id = @SteamId AND type = @Type AND removed = 0",
                new { SteamId = t.SteamID.ToString(), Type = type });

            // Also remove silence rows when ungagging/unmuting
            if (type == "gag" || type == "silence")
            {
                GaggedPlayers.Remove(t.SteamID);
                _ = CS2UltimodPlugin.Database.ExecuteAsync(
                    "UPDATE admin_comms SET removed = 1 WHERE steam_id = @SteamId AND type = 'silence' AND removed = 0",
                    new { SteamId = t.SteamID.ToString() });
            }
            if (type == "mute" || type == "silence")
            {
                MutedPlayers.Remove(t.SteamID);
                t.VoiceFlags = VoiceFlags.Normal;
                _ = CS2UltimodPlugin.Database.ExecuteAsync(
                    "UPDATE admin_comms SET removed = 1 WHERE steam_id = @SteamId AND type = 'silence' AND removed = 0",
                    new { SteamId = t.SteamID.ToString() });
            }

            Chat.Broadcast($"{AdminName(caller)} a {pastTense} {t.PlayerName}.");
        }
    }

    private static async Task SaveCommAsync(CCSPlayerController target, string adminId, string type, string reason, int duration)
    {
        var expiresAt = duration > 0
            ? DateTime.UtcNow.AddMinutes(duration).ToString("yyyy-MM-dd HH:mm:ss")
            : null;

        await CS2UltimodPlugin.Database.ExecuteAsync(
            """
            INSERT INTO admin_comms (steam_id, admin_id, type, reason, duration, expires_at)
            VALUES (@SteamId, @AdminId, @Type, @Reason, @Duration, @ExpiresAt)
            """,
            new
            {
                SteamId = target.SteamID.ToString(),
                AdminId = adminId,
                Type = type,
                Reason = reason,
                Duration = duration,
                ExpiresAt = expiresAt
            });
    }

    // ── Chat.HudCenterAll helper (if not in Chat class) ──────────────────────

    private static void HudCenterAll(string msg, float duration)
    {
        foreach (var p in PlayerExt.AllConnected())
            Chat.HudCenter(p, msg, duration);
    }

    // ── Internal DTOs ─────────────────────────────────────────────────────────

    private sealed class DisconnectedRow
    {
        public string steam_id { get; set; } = "";
        public string name { get; set; } = "";
        public string disconnected_at { get; set; } = "";
    }
}
