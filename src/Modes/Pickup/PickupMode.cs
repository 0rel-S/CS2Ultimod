// ── Wiring TODO ────────────────────────────────────────────────────────────────
// In CS2UltimodPlugin.Load(), before RunMigrationsAsync, add:
//
//   DatabaseRegistry.Register(new PickupMigration());
//
//   var pickupMode = new PickupMode.PickupMode(ModuleDirectory);
//   ((ModeManager)ModeManager).Register(pickupMode);
//
// ─────────────────────────────────────────────────────────────────────────────

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Utils;
using System.Text.Json;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Database migration for the Pickup mode tables.
/// Register via DatabaseRegistry.Register(new PickupMigration()) before RunMigrationsAsync.
/// </summary>
public sealed class PickupMigration : IMigration
{
    public string Id      => "pickup_v300";
    public int    Version => 300;

    public async Task UpAsync(IDatabase db)
    {
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS pickup_faceit_links (
                steam_id    TEXT NOT NULL PRIMARY KEY,
                faceit_name TEXT NOT NULL,
                faceit_id   TEXT,
                elo         INTEGER NOT NULL DEFAULT 0,
                updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS pickup_matches (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                map         TEXT NOT NULL,
                team_t      TEXT NOT NULL,
                team_ct     TEXT NOT NULL,
                score_t     INTEGER NOT NULL DEFAULT 0,
                score_ct    INTEGER NOT NULL DEFAULT 0,
                started_at  TEXT NOT NULL DEFAULT (datetime('now')),
                ended_at    TEXT,
                status      TEXT NOT NULL DEFAULT 'live'
            )
            """);
    }
}

/// <summary>
/// Active match record (in-memory).
/// </summary>
internal sealed class PickupMatch
{
    public long   Id       { get; set; }
    public string Map      { get; set; } = string.Empty;
    public FormedTeams Teams { get; set; } = new();
    public int    ScoreT   { get; set; }
    public int    ScoreCt  { get; set; }
}

/// <summary>
/// Main IGameMode implementation for Pickup (5v5) mode.
/// Orchestrates the full state machine from Idle through MatchEnd and BO3.
/// </summary>
public sealed class PickupMode : IGameMode, IDisposable
{
    public GameMode Mode => GameMode.Pickup;

    private readonly string _moduleDirectory;
    private PickupConfig _config = new();

    // Sub-systems
    private ReadySystem?    _ready;
    private PauseSystem?    _pause;
    private TeamBuilder?    _teamBuilder;
    private MapVote?        _mapVote;
    private BO3Manager?     _bo3;
    private FaceitClient?   _faceit;

    // State
    private readonly PickupStateMachine _sm = new();
    private TeamFormationMode _formationMode  = TeamFormationMode.None;
    private FormedTeams?      _teams;
    private PickupMatch?      _currentMatch;
    private CsTeam            _knifeWinner    = CsTeam.None;
    private readonly List<string> _playedMaps = [];

    // Knife vote state
    private bool _knifeVoteResolved;
    private bool _commandsRegistered;

    public PickupMode(string moduleDirectory)
    {
        _moduleDirectory = moduleDirectory;
    }

    // ── IGameMode ─────────────────────────────────────────────────────────────

    public Task OnEnterAsync(ModeContext ctx)
    {
        _config = PickupConfig.Load(_moduleDirectory);

        ResetAllSystems();
        RegisterCommands();
        SubscribeEvents();

        _sm.ForceIdle();
        EnterWarmup();

        return Task.CompletedTask;
    }

    public Task OnExitAsync(ModeContext ctx)
    {
        UnregisterCommands();
        CleanupSystems();
        _sm.ForceIdle();
        return Task.CompletedTask;
    }

    // ── State transitions ─────────────────────────────────────────────────────

    private void EnterWarmup()
    {
        if (!_sm.TryTransition(PickupPhase.Warmup))
        {
            _sm.ForceIdle();
            _sm.TryTransition(PickupPhase.Warmup);
        }

        _formationMode = TeamFormationMode.None;
        _teams         = null;
        _knifeWinner   = CsTeam.None;

        Server.ExecuteCommand("mp_warmup_start");
        Server.ExecuteCommand("mp_restartgame 1");
        Chat.Broadcast("=== PICKUP MODE ===");
        Chat.Broadcast("Utilisez !captain, !random ou !elopickup pour former les équipes.");
        Chat.Broadcast("Utilisez !faceit <username> pour lier votre compte Faceit.");
        Chat.Broadcast("Utilisez !ready quand vous êtes prêts (10 joueurs requis).");

        _ready?.StartHudUpdates();
    }

    private void StartKnifeRound()
    {
        if (!_sm.TryTransition(PickupPhase.KnifeRound)) return;

        // Apply teams first
        if (_teams != null)
            _teamBuilder?.ApplyTeams(_teams);

        Chat.Broadcast("=== COUTEAUX ! ===");
        Chat.Broadcast("L'équipe gagnante choisit son côté. Seuls couteaux/tasers autorisés.");

        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_give_player_c4 0");
        // Restrict to knife only via convars
        Server.ExecuteCommand("mp_restartgame 1");
    }

    private void EnterKnifeVote(CsTeam winner)
    {
        if (!_sm.TryTransition(PickupPhase.KnifeVote)) return;

        _knifeWinner      = winner;
        _knifeVoteResolved = false;

        var winnerName = winner == CsTeam.Terrorist ? "Terroristes" : "Counter-Terroristes";
        Chat.Broadcast($"=== COUTEAUX — {winnerName} gagnent ! ===");
        Chat.Broadcast("!stay pour garder votre côté, !swap pour changer.");

        // Open menu for each player on the winning team
        if (_teams != null)
        {
            var winnerIds = winner == CsTeam.Terrorist ? _teams.TeamT : _teams.TeamCt;
            foreach (var id in winnerIds)
            {
                var p = PlayerExt.FindBySteamId(id);
                if (p != null)
                    OpenKnifeVoteMenu(p);
            }
        }
    }

    private void OpenKnifeVoteMenu(CCSPlayerController player)
    {
        var menu = CS2UltimodPlugin.Menus.Create("Choisir votre côté");
        menu.AddItem("Garder mon côté (!stay)",  _ => ResolveKnifeVote(false));
        menu.AddItem("Changer de côté (!swap)",  _ => ResolveKnifeVote(true));
        menu.Open(player);
    }

    private void ResolveKnifeVote(bool swap)
    {
        if (_knifeVoteResolved) return;
        _knifeVoteResolved = true;

        if (swap && _teams != null)
        {
            _teamBuilder?.SwapTeams(_teams);
            Chat.Broadcast("Équipes inversées suite au vote couteau.");
        }
        else
        {
            Chat.Broadcast("Équipes maintenues.");
        }

        StartLive();
    }

    private void StartLive()
    {
        if (!_sm.TryTransition(PickupPhase.Live)) return;

        if (_teams != null)
            _teamBuilder?.ApplyTeams(_teams);

        Server.ExecuteCommand("mp_give_player_c4 1");
        Server.ExecuteCommand($"mp_maxrounds {12 * 2}"); // MR12 = 24 rounds total
        Server.ExecuteCommand("mp_restartgame 3");

        var map = _currentMatch?.Map ?? Server.MapName;
        Chat.Broadcast($"=== MATCH EN DIRECT — {map} ===");
        Chat.Broadcast("Bonne chance à tous !");

        _pause?.ResetForHalf();
    }

    private void EnterHalfTime()
    {
        if (!_sm.TryTransition(PickupPhase.HalfTime)) return;

        Chat.Broadcast("=== MI-TEMPS ===");

        if (_teams != null)
            _teamBuilder?.SwapTeams(_teams);

        _pause?.ResetForHalf();

        _ = Task.Delay(15_000).ContinueWith(_ => Server.NextFrame(StartLive2));
    }

    private void StartLive2()
    {
        if (!_sm.TryTransition(PickupPhase.Live2)) return;
        Chat.Broadcast("=== DEUXIÈME MI-TEMPS ===");
    }

    private async Task EnterMatchEndAsync(int scoreT, int scoreCt)
    {
        if (!_sm.TryTransition(PickupPhase.MatchEnd)) return;

        var winner = scoreT > scoreCt ? "Terroristes" : scoreCt > scoreT ? "Counter-Terroristes" : "Égalité";
        Chat.Broadcast($"=== FIN DU MATCH — T:{scoreT} CT:{scoreCt} — {winner} ===");

        // Save to DB
        if (_currentMatch != null)
        {
            await CS2UltimodPlugin.Database.ExecuteAsync("""
                UPDATE pickup_matches
                   SET score_t = @ScoreT, score_ct = @ScoreCt, ended_at = datetime('now'), status = 'finished'
                 WHERE id = @Id
                """,
                new { ScoreT = scoreT, ScoreCt = scoreCt, _currentMatch.Id });
        }

        Server.NextFrame(() =>
        {
            _sm.TryTransition(PickupPhase.BO3Vote);
            _bo3?.StartVote();
        });
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    private void SubscribeEvents()
    {
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(OnRoundEnd,       GameMode.Pickup);
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(OnRoundStart,   GameMode.Pickup);
        CS2UltimodPlugin.EventBus.Subscribe<MapStartEvent>(OnMapStart,       GameMode.Pickup);
    }

    private void OnRoundStart(RoundStartEvent evt)
    {
        if (_sm.Is(PickupPhase.KnifeRound))
            Chat.Broadcast("Manche couteaux — tuez tous les ennemis !");
    }

    private void OnRoundEnd(RoundEndEvent evt)
    {
        if (_sm.Is(PickupPhase.KnifeRound))
        {
            EnterKnifeVote(evt.Winner);
            return;
        }

        if (_sm.Is(PickupPhase.Live) || _sm.Is(PickupPhase.Live2))
        {
            if (_currentMatch != null)
            {
                _currentMatch.ScoreT  = evt.ScoreT;
                _currentMatch.ScoreCt = evt.ScoreCT;
            }

            // MR12: each half = 12 rounds → first half ends at round 12
            if (_sm.Is(PickupPhase.Live) && evt.ScoreT + evt.ScoreCT >= 12)
            {
                EnterHalfTime();
                return;
            }

            // Match ends at 13 wins (MR12), or on draw at 24 rounds
            if (evt.ScoreT >= 13 || evt.ScoreCT >= 13 ||
                (evt.ScoreT + evt.ScoreCT >= 24))
            {
                _ = EnterMatchEndAsync(evt.ScoreT, evt.ScoreCT);
            }
        }
    }

    private void OnMapStart(MapStartEvent evt)
    {
        if (_sm.Is(PickupPhase.Idle) || _sm.Is(PickupPhase.Warmup))
        {
            _sm.ForceIdle();
            EnterWarmup();
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private static readonly GameMode[] PickupOnly = [GameMode.Pickup];

    private void RegisterCommands()
    {
        if (_commandsRegistered) return;
        _commandsRegistered = true;
        var cmds = CS2UltimodPlugin.Commands;

        cmds.Register(new ChatCommand("ready", ["r"],    null,           "!ready [−f]",         CmdReady,    PickupOnly));
        cmds.Register(new ChatCommand("unready", null,   null,           "!unready",             CmdUnready,  PickupOnly));
        cmds.Register(new ChatCommand("faceit",  null,   null,           "!faceit <username>",   CmdFaceit,   PickupOnly));
        cmds.Register(new ChatCommand("captain", null,   null,           "!captain",             CmdCaptain,  PickupOnly));
        cmds.Register(new ChatCommand("random",  null,   null,           "!random",              CmdRandom,   PickupOnly));
        cmds.Register(new ChatCommand("elopickup",null,  null,           "!elopickup",           CmdEloPick,  PickupOnly));
        cmds.Register(new ChatCommand("stay",    null,   null,           "!stay",                CmdStay,     PickupOnly));
        cmds.Register(new ChatCommand("swap",    null,   null,           "!swap",                CmdSwap,     PickupOnly));
        cmds.Register(new ChatCommand("reshuffle",null,  null,           "!reshuffle",           CmdReshuffle,PickupOnly));
        cmds.Register(new ChatCommand("pause",   null,   null,           "!pause",               CmdPause,    PickupOnly));
        cmds.Register(new ChatCommand("unpause", null,   null,           "!unpause",             CmdUnpause,  PickupOnly));
        cmds.Register(new ChatCommand("bo3yes",  null,   null,           "!bo3yes",              CmdBo3Yes,   PickupOnly));
        cmds.Register(new ChatCommand("bo3no",   null,   null,           "!bo3no",               CmdBo3No,    PickupOnly));
    }

    private void UnregisterCommands()
    {
        // CommandRegistry doesn't support unregister — commands become no-ops when mode changes
        // because AvailableInModes check happens in the dispatcher.
        // Nothing to do here — the dispatcher handles mode filtering.
    }

    // -- Command handlers --

    private void CmdReady(CCSPlayerController player, string[] args)
    {
        // !ready -f → force all ready (requires @css/generic)
        if (args.Length > 0 && args[0] == "-f")
        {
            if (!CS2UltimodPlugin.Permissions.RequireFlag(player, "@css/generic")) return;
            _ready?.ForceAllReady();
            Chat.BroadcastSuccess("Tous les joueurs ont été forcés à prêt.");
            return;
        }

        if (!_sm.Is(PickupPhase.Warmup) && !_sm.Is(PickupPhase.ReadyCheck))
        {
            Chat.TellError(player, "La phase de préparation est terminée.");
            return;
        }

        var connected = PlayerExt.AllConnected().Count();
        if (connected < 10)
        {
            Chat.TellError(player, $"Nombre insuffisant de joueurs ({connected}/10).");
            return;
        }

        _ready?.SetReady(player, true);
        Chat.TellSuccess(player, $"Vous êtes prêt. ({_ready?.ReadyCount()}/{10})");
    }

    private void CmdUnready(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.Warmup) && !_sm.Is(PickupPhase.ReadyCheck))
        {
            Chat.TellError(player, "Vous ne pouvez pas vous désinscrire maintenant.");
            return;
        }

        _ready?.SetReady(player, false);
        Chat.Tell(player, "Vous n'êtes plus prêt.");
    }

    private void CmdFaceit(CCSPlayerController player, string[] args)
    {
        if (args.Length == 0)
        {
            Chat.TellError(player, "Usage: !faceit <username>");
            return;
        }

        var username = args[0];
        var steamId  = player.SteamID.ToString();

        _ = Task.Run(async () =>
        {
            try
            {
                var link = await _faceit!.FetchAndCacheAsync(username, steamId);
                Server.NextFrame(() =>
                {
                    if (link == null)
                        Chat.TellError(player, $"Compte Faceit introuvable pour '{username}'.");
                    else
                        Chat.TellSuccess(player, $"Compte Faceit lié: {link.FaceitName} (Elo: {link.Elo}).");
                });
            }
            catch (FaceitApiException ex)
            {
                Server.NextFrame(() => Chat.TellError(player, ex.Message));
            }
            catch (Exception)
            {
                Server.NextFrame(() => Chat.TellError(player, "Erreur lors de la connexion à Faceit."));
            }
        });
    }

    private void CmdCaptain(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.Warmup))
        {
            Chat.TellError(player, "La sélection des capitaines n'est disponible qu'en phase de chauffe.");
            return;
        }

        var connected = PlayerExt.AllConnected().Count();
        if (connected < 10)
        {
            Chat.TellError(player, $"Nombre insuffisant de joueurs ({connected}/10).");
            return;
        }

        _formationMode = TeamFormationMode.Captain;
        _teamBuilder?.ResetCaptains();
        _teamBuilder?.TryAddCaptain(player);
    }

    private void CmdRandom(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.Warmup))
        {
            Chat.TellError(player, "Commande disponible uniquement en phase de chauffe.");
            return;
        }

        var connected = PlayerExt.AllConnected().Count();
        if (connected < 10)
        {
            Chat.TellError(player, $"Nombre insuffisant de joueurs ({connected}/10).");
            return;
        }

        _formationMode = TeamFormationMode.Random;
        _teamBuilder?.StartRandomTeams(player);
    }

    private void CmdEloPick(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.Warmup))
        {
            Chat.TellError(player, "Commande disponible uniquement en phase de chauffe.");
            return;
        }

        var connected = PlayerExt.AllConnected().Count();
        if (connected < 10)
        {
            Chat.TellError(player, $"Nombre insuffisant de joueurs ({connected}/10).");
            return;
        }

        _formationMode = TeamFormationMode.Elo;
        _ = _teamBuilder!.StartEloTeamsAsync(player);
    }

    private void CmdStay(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.KnifeVote))
        {
            Chat.TellError(player, "Commande disponible uniquement lors du vote couteau.");
            return;
        }

        if (_teams == null) return;

        var winnerIds = _knifeWinner == CsTeam.Terrorist ? _teams.TeamT : _teams.TeamCt;
        if (!winnerIds.Contains(player.SteamID))
        {
            Chat.TellError(player, "Seule l'équipe gagnante des couteaux peut choisir.");
            return;
        }

        ResolveKnifeVote(false);
    }

    private void CmdSwap(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.KnifeVote))
        {
            Chat.TellError(player, "Commande disponible uniquement lors du vote couteau.");
            return;
        }

        if (_teams == null) return;

        var winnerIds = _knifeWinner == CsTeam.Terrorist ? _teams.TeamT : _teams.TeamCt;
        if (!winnerIds.Contains(player.SteamID))
        {
            Chat.TellError(player, "Seule l'équipe gagnante des couteaux peut choisir.");
            return;
        }

        ResolveKnifeVote(true);
    }

    private void CmdReshuffle(CCSPlayerController player, string[] args)
    {
        if (_formationMode != TeamFormationMode.Random)
        {
            Chat.TellError(player, "Commande disponible uniquement en mode aléatoire.");
            return;
        }

        _teamBuilder?.Reshuffle(player);
    }

    private void CmdPause(CCSPlayerController player, string[] args)
    {
        if (!_sm.IsLive())
        {
            Chat.TellError(player, "Vous ne pouvez faire une pause que pendant un match en direct.");
            return;
        }

        _pause?.TryPause(player);
    }

    private void CmdUnpause(CCSPlayerController player, string[] args)
    {
        var isAdmin = CS2UltimodPlugin.Permissions.HasFlag(player, "@css/generic");
        _pause?.TryUnpause(player, isAdmin);
    }

    private void CmdBo3Yes(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.BO3Vote))
        {
            Chat.TellError(player, "Aucun vote BO3 en cours.");
            return;
        }

        _bo3?.RegisterVote(player, true);
    }

    private void CmdBo3No(CCSPlayerController player, string[] args)
    {
        if (!_sm.Is(PickupPhase.BO3Vote))
        {
            Chat.TellError(player, "Aucun vote BO3 en cours.");
            return;
        }

        _bo3?.RegisterVote(player, false);
    }

    // ── Internal wiring ───────────────────────────────────────────────────────

    private void ResetAllSystems()
    {
        CleanupSystems();

        _faceit     = new FaceitClient(CS2UltimodPlugin.Database, _config.FaceitApiKey);
        _ready      = new ReadySystem(_sm, 10);
        _pause      = new PauseSystem(_config.PauseLimit);
        _teamBuilder = new TeamBuilder(CS2UltimodPlugin.Menus, _faceit, 5);
        _mapVote    = new MapVote(CS2UltimodPlugin.Menus, _config.MapPool);
        _bo3        = new BO3Manager(CS2UltimodPlugin.Menus);

        // Wire team formation completion → map vote/pick-ban
        _teamBuilder.OnTeamsFormed += OnTeamsFormed;

        // Wire map chosen → start knife
        _mapVote.OnMapChosen += OnMapChosen;

        // Wire BO3 vote result
        _bo3.OnVoteComplete += OnBo3VoteComplete;

        // Wire state changes
        _sm.OnPhaseChanged += OnPhaseChanged;
    }

    private void CleanupSystems()
    {
        _ready?.Dispose();
        _pause?.Dispose();
        _teamBuilder?.Dispose();
        _mapVote?.Dispose();
        _bo3?.Dispose();
        _faceit?.Dispose();

        _ready       = null;
        _pause       = null;
        _teamBuilder = null;
        _mapVote     = null;
        _bo3         = null;
        _faceit      = null;
    }

    private void OnPhaseChanged(PickupPhase from, PickupPhase to)
    {
        if (to == PickupPhase.Warmup)
            _ready?.StartHudUpdates();
        else
            _ready?.StopHudUpdates();
    }

    private void OnTeamsFormed(FormedTeams teams)
    {
        _teams = teams;
        _ready?.Reset();

        // Transition ready-check → knife
        if (_formationMode == TeamFormationMode.Captain)
        {
            // Captain mode: pick-ban before knife
            if (teams.CaptainCt != null && teams.CaptainT != null)
            {
                Chat.Broadcast("Équipes formées — début du pick-ban carte !");
                _mapVote?.StartPickBan(teams.CaptainCt, teams.CaptainT);
            }
            else
            {
                // No captains tagged: fall back to vote
                _mapVote?.StartMapVote();
            }
        }
        else
        {
            // Random / Elo: map vote
            _mapVote?.StartMapVote();
        }
    }

    private async void OnMapChosen(string map)
    {
        _playedMaps.Add(map);
        Chat.Broadcast($"Carte sélectionnée: {map}. Lancement dans 10s...");

        // Create match record in DB
        var teamsJson = _teams != null
            ? JsonSerializer.Serialize(_teams.TeamT.Select(id => id.ToString()).ToList())
            : "[]";
        var ctJson = _teams != null
            ? JsonSerializer.Serialize(_teams.TeamCt.Select(id => id.ToString()).ToList())
            : "[]";

        long matchId = 0;
        try
        {
            await CS2UltimodPlugin.Database.ExecuteAsync(
                "INSERT INTO pickup_matches (map, team_t, team_ct) VALUES (@Map, @TeamT, @TeamCt)",
                new { Map = map, TeamT = teamsJson, TeamCt = ctJson });

            var row = await CS2UltimodPlugin.Database.QuerySingleAsync<long?>(
                "SELECT last_insert_rowid()");
            matchId = row ?? 0;
        }
        catch { /* DB failure is non-fatal */ }

        Server.NextFrame(() =>
        {
            _currentMatch = new PickupMatch
            {
                Id    = matchId,
                Map   = map,
                Teams = _teams ?? new FormedTeams(),
            };

            _ = Task.Delay(10_000).ContinueWith(_ =>
                Server.NextFrame(() =>
                {
                    if (map != Server.MapName)
                        Server.ExecuteCommand($"map {map}");
                    else
                        StartKnifeRound();
                }));
        });
    }

    private void OnBo3VoteComplete(bool playAnother)
    {
        if (!playAnother)
        {
            _sm.TryTransition(PickupPhase.Idle);
            _playedMaps.Clear();
            EnterWarmup();
            return;
        }

        // BO3: pick-ban remaining maps, then switch
        _sm.TryTransition(PickupPhase.Warmup);

        if (_teams?.CaptainCt != null && _teams?.CaptainT != null)
        {
            _mapVote?.StartBo3PickBan(_teams.CaptainCt, _teams.CaptainT, _playedMaps);
        }
        else
        {
            _mapVote?.StartMapVote();
        }
    }

    public void Dispose() => CleanupSystems();
}
