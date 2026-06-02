using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Features.Allocator;
using CS2Ultimod.Modes.Mixte;
using CS2Ultimod.Modes.Retake.Editor;
using CS2Ultimod.Modes.Retake.Instadefuse;
using CS2Ultimod.Modes.Retake.Managers;
using CS2Ultimod.Modes.Retake.Models;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Modes.Retake;

public sealed class RetakeMode : IGameMode
{
    public GameMode Mode => GameMode.Retake;

    private readonly ILogger _logger;
    private readonly RetakeSpawnManager _spawnManager;
    private readonly RetakeGameManager _gameManager;
    private readonly SpawnEditor _spawnEditor;
    private readonly InstadefuseModule _instadefuse;
    private readonly string _spawnConfigDir;
    private readonly Random _random = new();

    private BombSite _currentSite;
    private bool _roundSetupDone;
    private readonly Dictionary<int, RetakeSpawn> _assignedSpawns = [];

    // Delegate hooks for Allocator (Track E will set this)
    public static Func<CCSPlayerController, BombSite, Task>? OnAllocatePlayer;

    public RetakeMode(ILogger logger, string spawnConfigDir)
    {
        _logger = logger;
        _spawnConfigDir = spawnConfigDir;
        _spawnManager = new RetakeSpawnManager(logger, spawnConfigDir);
        _gameManager = new RetakeGameManager();
        _spawnEditor = new SpawnEditor(_spawnManager);
        _instadefuse = new InstadefuseModule();
    }

    private bool _eventsRegistered;

    public void RegisterEvents()
    {
        if (_eventsRegistered) return;
        _eventsRegistered = true;
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(OnRoundStart, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(OnRoundEnd, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerSpawnEvent>(OnPlayerSpawn, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<BombPlantedEvent>(OnBombPlanted, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<BombDefusedEvent>(OnBombDefused, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<BombBeginDefuseEvent>(OnBombBeginDefuse, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<MapStartEvent>(OnMapStart);
        CS2UltimodPlugin.EventBus.Subscribe<InfernoStartburnEvent>(_instadefuse.OnInfernoStartburn, GameMode.Retake, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<InfernoExpireEvent>(_instadefuse.OnInfernoExpire, GameMode.Retake, GameMode.Mixte);

        // Load spawns at boot: SwitchToAsync is a no-op on startup if mode is already Retake,
        // so OnEnterAsync never fires. Load here so the first round works immediately.
        _spawnManager.LoadForMap(CounterStrikeSharp.API.Server.MapName);

        // Apply critical cvars now in case OnEnterAsync never fired (RCON reload path).
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_plant_c4_anywhere 0");
        Server.ExecuteCommand("mp_do_warmup_period 0");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_freezetime 1");
        Server.ExecuteCommand("mp_roundtime_defuse 0.75");
        Server.ExecuteCommand("mp_maxrounds 50");
        Server.ExecuteCommand("mp_match_can_clinch 0");
        Server.ExecuteCommand("mp_halftime 0");
        Server.ExecuteCommand("mp_overtime_enable 0");
        Server.ExecuteCommand("mp_match_end_restart 0");
        Server.ExecuteCommand("mp_timelimit 0");
        Server.ExecuteCommand("mp_round_restart_delay 1");
        // Permet de porter 2× chaque type de grenade (default 1, sauf flash = 2).
        // Nécessaire pour que les superhéros Boum / Fumeur / Pyromane fonctionnent
        // (sans ça, la 2e grenade donnée par GiveNamedItem tombe au sol).
        Server.ExecuteCommand("ammo_grenade_limit_default 2");
        Server.ExecuteCommand("ammo_grenade_limit_total 6");
    }

    public Task OnEnterAsync(ModeContext ctx)
    {
        _logger.LogInformation("[Retake] Entering retake mode on {Map}", ctx.MapName);
        _spawnManager.LoadForMap(ctx.MapName);
        _gameManager.Reset();
        RegisterEvents();

        _spawnEditor.RegisterCommands();

        // Server settings for retake
        Server.ExecuteCommand("mp_respawn_on_death_t 0");
        Server.ExecuteCommand("mp_respawn_on_death_ct 0");
        Server.ExecuteCommand("mp_freezetime 1");
        Server.ExecuteCommand("mp_roundtime_defuse 0.75");
        Server.ExecuteCommand("mp_maxrounds 50");
        Server.ExecuteCommand("mp_match_can_clinch 0");
        Server.ExecuteCommand("mp_halftime 0");
        Server.ExecuteCommand("mp_overtime_enable 0");
        Server.ExecuteCommand("mp_match_end_restart 0");
        Server.ExecuteCommand("mp_timelimit 0");
        Server.ExecuteCommand("mp_round_restart_delay 1");
        Server.ExecuteCommand("mp_free_armor 0");
        Server.ExecuteCommand("mp_buy_anywhere 0");
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_plant_c4_anywhere 0");
        Server.ExecuteCommand("mp_do_warmup_period 0");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("ammo_grenade_limit_default 2");
        Server.ExecuteCommand("ammo_grenade_limit_total 6");
        // Reset le score/round pour sortir d'un état overtime hérité d'un mode précédent.
        // mp_overtime_enable 0 empêche d'entrer en OT mais ne sort pas d'une OT en cours.
        Server.ExecuteCommand("mp_restartgame 1");

        if (!_spawnManager.HasConfig)
            Chat.Broadcast($"[Retake] Attention : aucune config de spawns pour cette map. Mode retake désactivé.");

        return Task.CompletedTask;
    }

    public Task OnExitAsync(ModeContext ctx)
    {
        _logger.LogInformation("[Retake] Exiting retake mode");
        _gameManager.Reset();
        _assignedSpawns.Clear();
        _roundSetupDone = false;
        return Task.CompletedTask;
    }

    private void OnRoundStart(RoundStartEvent evt)
    {
        // Always reset instadefuse state on round start, regardless of mode/config gates.
        _instadefuse.OnRoundStart();

        // Re-asserter les cvars critiques chaque round : la cfg dathost
        // (server.cfg/autoexec) tend à les réécrire aux défauts compétitifs
        // après nos Load/OnEnter (notamment mp_maxrounds=24, mp_halftime=1,
        // mp_match_can_clinch=1 → match qui se termine à 13).
        Server.ExecuteCommand("mp_freezetime 1");
        Server.ExecuteCommand("mp_maxrounds 50");
        Server.ExecuteCommand("mp_match_can_clinch 0");
        Server.ExecuteCommand("mp_halftime 0");
        Server.ExecuteCommand("mp_match_end_restart 0");
        Server.ExecuteCommand("mp_timelimit 0");
        Server.ExecuteCommand("mp_overtime_enable 0");

        if (CS2UltimodPlugin.ModeManager.Current == GameMode.Mixte && !MixteMode.IsRetakeRound)
            return;
        if (!_spawnManager.HasConfig) return;

        // Garde anti double round_start : à l'entrée en mode, mp_restartgame émet un
        // round_start supplémentaire en plus du round déjà en cours. Sans garde on
        // tirait deux sites aléatoires différents, téléportait deux fois et posait
        // deux bombes. On ne configure le round qu'une fois ; reset sur RoundEnd.
        if (_roundSetupDone) return;
        _roundSetupDone = true;

        _currentSite = _random.Next(2) == 0 ? BombSite.A : BombSite.B;
        var spawns = _spawnManager.GetSpawnsForSite(_currentSite);

        if (spawns.Count == 0)
        {
            Chat.Broadcast($"[Retake] Aucun spawn configuré pour le site {_currentSite}.");
            return;
        }

        var tSpawns  = spawns.Where(s => s.CsTeam == CsTeam.Terrorist).OrderBy(_ => _random.Next()).ToList();
        var ctSpawns = spawns.Where(s => s.CsTeam == CsTeam.CounterTerrorist).OrderBy(_ => _random.Next()).ToList();
        var planterSpawns = tSpawns.Where(s => s.CanBePlanter).ToList();

        var roundLabel = AllocatorModule.CurrentRoundType switch
        {
            RoundType.Pistol  => "Pistolet",
            RoundType.HalfBuy => "Semi-achat",
            RoundType.FullBuy => "Full-achat",
            _                 => "",
        };
        Chat.Broadcast($"Bombe posée sur le \x04site {_currentSite}\x01 !");
        Chat.Broadcast($"Round : \x04{roundLabel}\x01");

        // PlayerSpawn fires BEFORE RoundStart in CS2 — pawns are already valid here.
        // Use NextFrame to ensure engine has finished round-start setup.
        Server.NextFrame(() =>
        {
            CS2UltimodPlugin.Menus.ShowBanner($"SITE {_currentSite}  —  {roundLabel}", 4f);
            // Ensure no T can carry or plant a real C4 (guards against stale cvar state after RCON reload)
            Server.ExecuteCommand("mp_give_player_c4 0");
            Server.ExecuteCommand("mp_plant_c4_anywhere 0");

            // Remove any weapon_c4 the game may have spawned for T players
            foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CC4>("weapon_c4"))
                c4.Remove();

            // Include bots: InTeam excludes bots, use GetPlayers directly
            var ts  = Utilities.GetPlayers().Where(p => p.IsValid && p.Team == CsTeam.Terrorist).ToList();
            var cts = Utilities.GetPlayers().Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist).ToList();

            _logger.LogInformation("[Retake] Teleporting site={Site} T={T} CT={CT}", _currentSite, ts.Count, cts.Count);

            _assignedSpawns.Clear();
            for (var i = 0; i < ts.Count; i++)
            {
                if (tSpawns.Count == 0) break;
                var spawn = tSpawns[i % tSpawns.Count];
                _assignedSpawns[ts[i].Slot] = spawn;
                ts[i].PlayerPawn.Value?.Teleport(spawn.Position, spawn.Angle, new Vector(0, 0, 0));
            }
            for (var i = 0; i < cts.Count; i++)
            {
                if (ctSpawns.Count == 0) break;
                var spawn = ctSpawns[i % ctSpawns.Count];
                _assignedSpawns[cts[i].Slot] = spawn;
                cts[i].PlayerPawn.Value?.Teleport(spawn.Position, spawn.Angle, new Vector(0, 0, 0));
            }

            var planter = ts.FirstOrDefault(p => p.PawnIsAlive);
            if (planterSpawns.Count > 0)
                SpawnPlantedBomb(planterSpawns[_random.Next(planterSpawns.Count)].Position, _currentSite, planter);
        });
    }

    private void SpawnPlantedBomb(Vector position, BombSite bombsite, CCSPlayerController? planter)
    {
        // Ceinture de sécurité : ne jamais laisser deux planted_c4 coexister.
        // Retire toute bombe déjà présente avant d'en créer une nouvelle.
        foreach (var old in Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4"))
            old.Remove();

        var bomb = Utilities.CreateEntityByName<CPlantedC4>("planted_c4");
        if (bomb == null || !bomb.IsValid)
        {
            _logger.LogWarning("[Retake] SpawnPlantedBomb: CreateEntityByName returned null/invalid");
            return;
        }

        if (bomb.AbsOrigin == null)
        {
            _logger.LogWarning("[Retake] SpawnPlantedBomb: AbsOrigin is null");
            return;
        }

        bomb.AbsOrigin.X = position.X;
        bomb.AbsOrigin.Y = position.Y;
        bomb.AbsOrigin.Z = position.Z;
        bomb.HasExploded = false;
        bomb.BombSite = (int)bombsite;
        bomb.BombTicking = true;
        bomb.CannotBeDefused = false;
        bomb.DispatchSpawn();

        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        if (gameRules != null)
        {
            gameRules.BombPlanted = true;
            gameRules.BombDefused = false;
        }

        if (planter != null && planter.IsValid)
        {
            var eventPtr = NativeAPI.CreateEvent("bomb_planted", true);
            NativeAPI.SetEventPlayerController(eventPtr, "userid", planter.Handle);
            NativeAPI.SetEventInt(eventPtr, "site", (int)bombsite);
            NativeAPI.FireEvent(eventPtr, false);
        }

        _logger.LogInformation("[Retake] Bomb spawned at {X} {Y} {Z} site={Site}", position.X, position.Y, position.Z, bombsite);
        _instadefuse.OnBombPlanted();
    }

    private void OnRoundEnd(RoundEndEvent evt)
    {
        _roundSetupDone = false;
        _gameManager.OnRoundEnd(evt.Winner);
    }

    private void OnPlayerSpawn(PlayerSpawnEvent evt)
    {
        if (CS2UltimodPlugin.ModeManager.Current == GameMode.Mixte && !MixteMode.IsRetakeRound)
            return;
        var player = evt.Player;
        if (!player.IsValid || player.IsBot) return;

        // Allocate weapons — falls back to basic allocation if allocator not set
        if (OnAllocatePlayer != null)
            _ = Task.Run(() => OnAllocatePlayer(player, _currentSite));
        else
            BasicAllocate(player);
    }

    private void BasicAllocate(CCSPlayerController player)
    {
        player.GiveNamedItem("item_kevlar");
        player.GiveNamedItem("item_assaultsuit");

        if (player.Team == CsTeam.Terrorist)
        {
            player.GiveNamedItem("weapon_ak47");
            player.GiveNamedItem("weapon_deagle");
        }
        else if (player.Team == CsTeam.CounterTerrorist)
        {
            player.GiveNamedItem("weapon_m4a1_silencer");
            player.GiveNamedItem("weapon_deagle");

            var pawn = player.PlayerPawn.Value;
            if (pawn?.ItemServices != null)
            {
                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                itemServices.HasDefuser = true;
            }
        }

        player.GiveNamedItem("weapon_knife");
    }

    private void OnBombPlanted(BombPlantedEvent evt)
        => _instadefuse.OnBombPlanted();

    private void OnBombDefused(BombDefusedEvent evt) { }

    private void OnBombBeginDefuse(BombBeginDefuseEvent evt)
        => _instadefuse.OnBeginDefuse(evt.Defuser);

    // Map ASCII letters to mathematical sans-serif bold unicode block (𝗔=U+1D5D4 / 𝗮=U+1D5EE).
    private static string ToBoldSans(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (ch >= 'A' && ch <= 'Z') sb.Append(char.ConvertFromUtf32(0x1D5D4 + (ch - 'A')));
            else if (ch >= 'a' && ch <= 'z') sb.Append(char.ConvertFromUtf32(0x1D5EE + (ch - 'a')));
            else if (ch >= '0' && ch <= '9') sb.Append(char.ConvertFromUtf32(0x1D7EC + (ch - '0')));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private void OnMapStart(MapStartEvent evt)
    {
        _spawnManager.LoadForMap(evt.MapName);
        Server.ExecuteCommand("mp_do_warmup_period 0");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_freezetime 0");
        Server.ExecuteCommand("mp_roundtime_defuse 0.75");
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_plant_c4_anywhere 0");
    }
}
