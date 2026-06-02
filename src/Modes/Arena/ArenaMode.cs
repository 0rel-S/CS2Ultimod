using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Features.Votes;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Modes.Arena;

// Mode Arena : multi-1v1 en ladder. Inspiré de rockCityMath/CS2-Multi-1v1 (MIT),
// réécrit pour l'archi CS2Ultimod (event bus, IGameMode) et rendu map-agnostique
// (détection auto des paires de spawns sur n'importe quelle aim map).
//
// Principe : N arènes classées. Chaque round est un 1v1 par arène (P1=T, P2=CT).
// Le vainqueur d'une arène monte d'un cran, le perdant descend. Les deux meilleurs
// gagnants se retrouvent dans l'arène 1. Les nouveaux arrivants entrent par le bas.
public sealed class ArenaMode : IGameMode
{
    public GameMode Mode => GameMode.Arena;

    private readonly Random _random = new();
    private readonly List<Arena> _arenas = [];
    private readonly Queue<CCSPlayerController> _waiting = new();
    // slot → arène, pour retrouver l'arène d'un joueur sur spawn/mort en O(1).
    private readonly Dictionary<int, Arena> _bySlot = [];

    private bool _eventsRegistered;
    private bool _seeded;
    // Empêche de terminer le round plusieurs fois (chaque mort déclenche la vérif).
    private bool _ending;

    public void RegisterEvents()
    {
        if (_eventsRegistered) return;
        _eventsRegistered = true;
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(OnRoundStart, GameMode.Arena);
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(OnRoundEnd, GameMode.Arena);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerSpawnEvent>(OnPlayerSpawn, GameMode.Arena);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerDeathEvent>(OnPlayerDeath, GameMode.Arena);
        // Sur changement de map (y compris celle qu'on charge nous-mêmes) : re-détecter
        // les arènes, et ré-asserter les cvars après que dathost ait rechargé server.cfg
        // (~3s, comme StuffMode), sinon nos cvars arena sont écrasés aux défauts compétitifs.
        CS2UltimodPlugin.EventBus.Subscribe<MapStartEvent>(_ =>
        {
            // Les entités de spawn ne sont PAS encore peuplées au MapStart d'un chargement
            // frais → on ne détecte pas ici (donnerait 0). On invalide l'état et la détection
            // se fera au 1er RoundStart (entités garanties présentes). Les cvars sont
            // ré-assertés après ~3s, une fois que dathost a rechargé server.cfg.
            _seeded = false;
            _arenas.Clear();
            _bySlot.Clear();
            new CounterStrikeSharp.API.Modules.Timers.Timer(3.0f, ApplyArenaCvars);
        }, GameMode.Arena);
    }

    public Task OnEnterAsync(ModeContext ctx)
    {
        RegisterEvents();
        _seeded = false;
        ApplyArenaCvars(); // applique les cvars + EnsureBots

        // Charge la map d'arène par défaut (1ère du pool Arena). La détection des
        // arènes + le restart se font au MapStart de la nouvelle map. Si aucun pool
        // n'est configuré, on reste sur la map courante (détection immédiate).
        var workshopId = DefaultArenaWorkshopId();
        if (workshopId != null)
        {
            CS2UltimodPlugin.Log?.LogInformation("[Arena] Chargement de la map d'arène par défaut (workshop {Id})", workshopId);
            Server.ExecuteCommand($"host_workshop_map {workshopId}");
        }
        else
        {
            DetectArenas();
            Server.ExecuteCommand("mp_restartgame 1");
        }
        return Task.CompletedTask;
    }

    // Récupère l'ID workshop de la 1ère map du pool Arena (format "Nom=ID").
    private static string? DefaultArenaWorkshopId()
    {
        var pool = VoteModule.GetMapPool(GameMode.Arena);
        if (pool.Count == 0) return null;
        var first = pool[0];
        var idx = first.IndexOf('=');
        return idx > 0 && idx < first.Length - 1 ? first[(idx + 1)..].Trim() : null;
    }

    public Task OnExitAsync(ModeContext ctx)
    {
        _arenas.Clear();
        _waiting.Clear();
        _bySlot.Clear();
        _seeded = false;
        RestoreCvars();
        Server.ExecuteCommand("mp_restartgame 1");
        return Task.CompletedTask;
    }

    // ── Détection des spawns ────────────────────────────────────────────────
    // Apparie chaque spawn CT au spawn T le plus proche (1 paire = 1 arène).
    // Map-agnostique : marche sur toute map ayant des paires de spawns proches
    // (aim_*, 1v1 maps). Snapshot des positions, pas de handle d'entité conservé.
    private void DetectArenas()
    {
        _arenas.Clear();
        _bySlot.Clear();

        var ctSpawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_counterterrorist")
            .Where(s => s.CBodyComponent?.SceneNode?.AbsOrigin != null).ToList();
        var tSpawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
            .Where(s => s.CBodyComponent?.SceneNode?.AbsOrigin != null).ToList();

        foreach (var ct in ctSpawns)
        {
            var ctPos = ct.CBodyComponent!.SceneNode!.AbsOrigin;
            SpawnPoint? closest = null;
            var best = float.MaxValue;
            foreach (var t in tSpawns)
            {
                var tPos = t.CBodyComponent!.SceneNode!.AbsOrigin;
                var d = DistSq(ctPos, tPos);
                if (d < best) { best = d; closest = t; }
            }
            if (closest == null) continue;

            var ctAng = ct.AbsRotation ?? new QAngle(0, 0, 0);
            var tAng = closest.AbsRotation ?? new QAngle(0, 0, 0);
            var tPos2 = closest.CBodyComponent!.SceneNode!.AbsOrigin;
            _arenas.Add(new Arena(new ArenaSpawnPair(
                new Vector(ctPos.X, ctPos.Y, ctPos.Z), new QAngle(ctAng.X, ctAng.Y, ctAng.Z),
                new Vector(tPos2.X, tPos2.Y, tPos2.Z), new QAngle(tAng.X, tAng.Y, tAng.Z))));
        }

        CS2UltimodPlugin.Log?.LogInformation("[Arena] Détecté {N} arènes sur {Map}", _arenas.Count, Server.MapName);
    }

    // ── Cycle de round ──────────────────────────────────────────────────────

    private void OnRoundStart(RoundStartEvent evt)
    {
        // Nouveau round : on réarme la terminaison automatique.
        _ending = false;
        // Ré-asserte le quota de bots à chaque round : rattrape un éventuel rechargement
        // de server.cfg par dathost survenu après la fenêtre des 3 s (sinon les bots qu'il
        // ajoute resteraient jusqu'au prochain RebuildLadder). Ne kicke que l'excédent.
        EnsureBots();
        // Détection (re)faite ici si nécessaire : au 1er round après un chargement de
        // map, les entités de spawn sont peuplées (contrairement au MapStart).
        if (_arenas.Count == 0) DetectArenas();
        // Pendant la transition de map, un RoundStart peut survenir avant que les
        // entités soient peuplées (détection = 0). On ne seede pas dans ce cas : on
        // réessaiera au round suivant, quand les arènes existeront vraiment.
        if (_arenas.Count == 0) return;
        if (_seeded) return;
        // Premier round du mode : on remplit la file avec tous les présents et on
        // assigne avant que les spawns ne soient téléportés.
        foreach (var p in Utilities.GetPlayers())
            if (p is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected })
                _waiting.Enqueue(p);
        // spawnNow : les joueurs ont déjà spawn pour ce round (PlayerSpawn précède
        // RoundStart) → on les respawn dans leur arène tout de suite, sinon ils
        // attendraient le round suivant. En régime établi (RoundEnd) c'est inutile :
        // l'assignation précède le spawn du round suivant.
        RebuildLadder(spawnNow: true);
        _seeded = true;
    }

    private void OnRoundEnd(RoundEndEvent evt)
    {
        if (!_seeded) return;
        RebuildLadder();
    }

    private void OnPlayerSpawn(PlayerSpawnEvent evt)
    {
        var player = evt.Player;
        if (!player.IsValid) return;

        // Réel comme bot de sparring : s'il est assigné à une arène, on le (re)place.
        if (_bySlot.TryGetValue(player.Slot, out var arena))
        {
            Server.NextFrame(() => { if (player.IsValid) arena.SpawnPlayer(player); });
            return;
        }

        // Bot non assigné : soit résiduel (en attente de kick), soit un bot de sparring qui
        // vient d'apparaître alors qu'un vrai joueur attend un adversaire. Dans ce 2e cas on
        // reboucle le round pour l'intégrer : MaybeEndRound ne terminera que si AUCUN duel
        // n'est en cours (une arène à un seul joueur est déjà "finie") — sinon le bot sera
        // assigné au prochain RebuildLadder sans rien interrompre. Sans ça, un joueur seul
        // resterait planté jusqu'au timer faute de mort pour déclencher la vérification.
        if (player.IsBot)
        {
            if (_seeded) Server.NextFrame(MaybeEndRound);
            return;
        }

        // Joueur non assigné (vient de se connecter) : en file d'attente, mis en
        // spectateur pour ne pas fausser la condition de fin de round. Sera placé
        // au prochain RebuildLadder (fin du round courant).
        if (_seeded && !_waiting.Contains(player))
        {
            _waiting.Enqueue(player);
            Server.NextFrame(() => { if (player.IsValid) player.ChangeTeam(CsTeam.Spectator); });
        }
    }

    private void OnPlayerDeath(PlayerDeathEvent evt)
    {
        if (_bySlot.TryGetValue(evt.Victim.Slot, out var arena))
            arena.OnDeath(evt.Victim);
        // Différé d'une frame : laisse l'état "alive" du pawn de la victime se mettre
        // à jour avant d'évaluer HasFinished.
        Server.NextFrame(MaybeEndRound);
    }

    // En multi-1v1, le moteur ne termine le round que quand une équipe entière est
    // morte (ou au timer) : après résolution des duels il reste un mix de T et CT
    // vivants, donc le round ne finit jamais tout seul. On le termine manuellement dès
    // que toutes les arènes avec de vrais joueurs ont fini leur duel — ce qui relance
    // le cycle (RoundEnd → RebuildLadder → respawn au round suivant).
    private void MaybeEndRound()
    {
        if (!_seeded || _ending) return;
        var active = _arenas.Where(a => a.HasRealPlayers).ToList();
        if (active.Count == 0 || active.Any(a => !a.HasFinished)) return;

        var rules = GetGameRules();
        if (rules == null) return;

        // Raison cosmétique (scoreboard) : l'équipe avec le plus de survivants.
        var tAlive = PlayerExt.AllAlive().Count(p => p.Team == CsTeam.Terrorist);
        var ctAlive = PlayerExt.AllAlive().Count(p => p.Team == CsTeam.CounterTerrorist);
        var reason = ctAlive > tAlive ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin;

        _ending = true;
        rules.TerminateRound(2.0f, reason);
    }

    private static CCSGameRules? GetGameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;

    // ── Ladder ──────────────────────────────────────────────────────────────
    // Reconstruit le classement : gagnants/perdants extraits des arènes du round
    // écoulé, recomposés (2 meilleurs gagnants en arène 1, puis alternance
    // gagnant-d'en-dessous / perdant-d'au-dessus), nouveaux arrivants en bas.
    private void RebuildLadder(bool spawnNow = false)
    {
        // Seuls les vrais joueurs participent au ladder : un bot de sparring ne monte ni
        // ne descend (s'il "gagne" contre un réel, le réel redescend et le bot disparaît
        // simplement du classement, re-comblé au placement ci-dessous).
        var winners = new Queue<CCSPlayerController>();
        var losers = new Queue<CCSPlayerController>();
        foreach (var arena in _arenas)
        {
            var r = arena.GetResult();
            if (r.Type == ArenaResultType.Win)
            {
                if (r.Winner is { IsBot: false }) winners.Enqueue(r.Winner);
                if (r.Loser is { IsBot: false }) losers.Enqueue(r.Loser);
            }
            else if (r.Type == ArenaResultType.NoOpponent)
            {
                if (r.Winner is { IsBot: false }) winners.Enqueue(r.Winner);
            }
        }

        var ranked = new Queue<CCSPlayerController>();
        if (winners.Count > 1) { ranked.Enqueue(winners.Dequeue()); ranked.Enqueue(winners.Dequeue()); }
        while (winners.Count > 0)
        {
            ranked.Enqueue(winners.Dequeue());
            if (losers.Count > 0) ranked.Enqueue(losers.Dequeue());
        }
        while (losers.Count > 0) ranked.Enqueue(losers.Dequeue());
        while (_waiting.Count > 0) ranked.Enqueue(_waiting.Dequeue());

        // Filtre les déconnectés / invalides accumulés.
        var players = new Queue<CCSPlayerController>(
            ranked.Where(p => p is { IsValid: true, Connected: PlayerConnectedState.Connected }));

        // Réserve de bots présents, utilisés comme adversaires de sparring pour combler
        // l'arène à un seul vrai joueur (effectif impair). EnsureBots() maintient leur
        // nombre (au plus 1) ; on ne crée donc jamais de duel bot contre bot.
        var spareBots = new Queue<CCSPlayerController>(
            Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: true }));

        _bySlot.Clear();
        Shuffle(_arenas);

        var placed = new List<CCSPlayerController>();
        var arenasUsed = 0;
        for (var i = 0; i < _arenas.Count; i++)
        {
            var arena = _arenas[i];
            CCSPlayerController? p1 = players.Count > 0 ? players.Dequeue() : null;
            CCSPlayerController? p2 = players.Count > 0 ? players.Dequeue() : null;
            // Un seul vrai joueur dans cette arène → on lui donne un bot adversaire.
            if (p1 != null && p2 == null && spareBots.Count > 0) p2 = spareBots.Dequeue();
            var rank = i + 1;
            var roundType = ArenaRoundType.All[_random.Next(ArenaRoundType.All.Length)];
            arena.Assign(p1, p2, rank, roundType, _random.Next(2) == 0);

            if (p1 != null) { _bySlot[p1.Slot] = arena; Announce(p1, arena, p2); placed.Add(p1); }
            if (p2 != null) { _bySlot[p2.Slot] = arena; Announce(p2, arena, p1); placed.Add(p2); }
            if (p1 != null || p2 != null) arenasUsed++;
        }

        CS2UltimodPlugin.Log?.LogInformation(
            "[Arena] Ladder reconstruit : {Placed} joueur(s) répartis sur {Used}/{Total} arènes",
            placed.Count, arenasUsed, _arenas.Count);

        // Ajuste le nombre de bots de sparring pour le prochain round (l'effectif réel
        // a pu changer). Appelé en fin pour ne pas perturber l'assignation courante.
        EnsureBots();

        // Respawn différé (~0.5s, laisse l'assignation d'équipe se propager) pour que
        // les joueurs déjà en jeu soient (re)placés dans leur arène immédiatement.
        // Le respawn déclenche PlayerSpawn → SpawnPlayer (téléport + armes).
        if (spawnNow && placed.Count > 0)
        {
            new CounterStrikeSharp.API.Modules.Timers.Timer(0.5f, () =>
            {
                foreach (var p in placed)
                    // Bots inclus : ils n'ont pas d'état Connected fiable, on se contente de IsValid.
                    if (p is { IsValid: true, Team: > CsTeam.Spectator }
                        && (p.IsBot || p.Connected == PlayerConnectedState.Connected))
                        p.Respawn();
            });
        }
    }

    private static void Announce(CCSPlayerController player, Arena arena, CCSPlayerController? opponent)
    {
        // Pas de spam chat ni de manip d'un bot de sparring.
        if (player.IsBot) return;
        var opp = opponent is { IsValid: true } ? opponent.PlayerName : "aucun adversaire";
        player.Clan = $"ARÈNE {arena.Rank}";
        Chat.Tell(player, $"Arène \x04{arena.Rank}\x01 ({arena.RoundType.Name}) contre \x04{opp}\x01");
    }

    // ── Cvars ────────────────────────────────────────────────────────────────

    // Maintient le bon nombre de bots de sparring. En multi-1v1, au plus UNE arène peut
    // se retrouver avec un seul vrai joueur (effectif impair) : on garde donc 1 bot quand
    // le nombre de vrais joueurs est impair, 0 sinon — jamais de duel bot contre bot, et
    // les vrais joueurs s'affrontent entre eux en priorité. Doit être ré-asserté après le
    // rechargement de server.cfg par dathost (qui réécrit bot_quota), d'où l'appel depuis
    // ApplyArenaCvars (programmé à MapStart+3s) et à chaque RebuildLadder (effectif qui change).
    private static void EnsureBots()
    {
        var players = Utilities.GetPlayers();
        var reals = players.Count(
            p => p is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected });
        var bots = players.Count(p => p is { IsValid: true, IsBot: true });
        var wanted = reals % 2 == 1 ? 1 : 0;
        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand($"bot_quota {wanted}");
        // bot_quota seul ne retire pas assez vite l'excédent (server.cfg dathost en ajoute
        // beaucoup au démarrage) → on kicke dès qu'il y en a trop, puis bot_quota recrée le
        // nombre voulu. En régime établi (1 voulu, 1 présent) : aucun kick, le sparring reste.
        if (bots > wanted) Server.ExecuteCommand("bot_kick");
    }

    private static void ApplyArenaCvars()
    {
        EnsureBots();
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmuptime 0");
        Server.ExecuteCommand("mp_warmup_end");
        // Coupe la cinématique d'intro compétitive (fige les joueurs ~6.5s au début
        // de match) — à bannir en arena où chaque round est un duel court.
        Server.ExecuteCommand("mp_team_intro_time 0");
        // Empêche le jeu de donner ses armes par défaut : on équipe nous-mêmes selon
        // le type de round (sinon le joueur a un pistolet en trop). Aligné sur K4-Arenas.
        Server.ExecuteCommand("mp_t_default_primary \"\"");
        Server.ExecuteCommand("mp_t_default_secondary \"\"");
        Server.ExecuteCommand("mp_ct_default_primary \"\"");
        Server.ExecuteCommand("mp_ct_default_secondary \"\"");
        Server.ExecuteCommand("mp_free_armor 0");
        Server.ExecuteCommand("mp_equipment_reset_rounds 0");
        Server.ExecuteCommand("mp_forcecamera 0");
        Server.ExecuteCommand("mp_join_grace_time 0");
        Server.ExecuteCommand("mp_respawn_immunitytime 0");
        Server.ExecuteCommand("mp_autokick 0");
        Server.ExecuteCommand("mp_freezetime 2");
        Server.ExecuteCommand("mp_roundtime 1.5");
        Server.ExecuteCommand("mp_roundtime_defuse 1.5");
        Server.ExecuteCommand("mp_round_restart_delay 2");
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_death_drop_gun 0");
        Server.ExecuteCommand("mp_death_drop_grenade 0");
        Server.ExecuteCommand("mp_respawn_on_death_ct 0");
        Server.ExecuteCommand("mp_respawn_on_death_t 0");
        Server.ExecuteCommand("mp_buy_anywhere 0");
        Server.ExecuteCommand("mp_buytime 0");
        Server.ExecuteCommand("mp_startmoney 0");
        Server.ExecuteCommand("mp_maxmoney 0");
        Server.ExecuteCommand("mp_solid_teammates 1");
        Server.ExecuteCommand("mp_teammates_are_enemies 0");
        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_limitteams 0");
        Server.ExecuteCommand("mp_maxrounds 0");
        Server.ExecuteCommand("mp_match_can_clinch 0");
        Server.ExecuteCommand("mp_halftime 0");
        Server.ExecuteCommand("mp_timelimit 0");
        Server.ExecuteCommand("mp_overtime_enable 0");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0");
    }

    private static void RestoreCvars()
    {
        Server.ExecuteCommand("mp_autoteambalance 1");
        Server.ExecuteCommand("mp_limitteams 2");
        Server.ExecuteCommand("mp_startmoney 800");
        Server.ExecuteCommand("mp_maxmoney 16000");
        Server.ExecuteCommand("mp_buytime 20");
        Server.ExecuteCommand("mp_give_player_c4 1");
        Server.ExecuteCommand("mp_death_drop_gun 1");
        Server.ExecuteCommand("mp_death_drop_grenade 1");
        Server.ExecuteCommand("mp_freezetime 15");
        Server.ExecuteCommand("mp_roundtime 1.92");
        Server.ExecuteCommand("mp_roundtime_defuse 1.92");
    }

    // ── Utils ────────────────────────────────────────────────────────────────
    private static float DistSq(Vector a, Vector b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private void Shuffle(IList<Arena> list)
    {
        for (var n = list.Count - 1; n > 0; n--)
        {
            var k = _random.Next(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }
}
