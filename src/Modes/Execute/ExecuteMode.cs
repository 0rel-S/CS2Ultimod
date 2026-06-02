using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod;
using CS2Ultimod.Core;
using Microsoft.Extensions.Logging;
using CS2Ultimod.Modes.Mixte;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Modes.Execute.Editor;
using CS2Ultimod.Modes.Execute.Managers;
using CS2Ultimod.Modes.Execute.Models;
using CS2Ultimod.Modes.Execute.Models.Enums;

namespace CS2Ultimod.Modes.Execute;

/// <summary>
/// Track B — Execute Mode.
/// Implements the IGameMode contract and orchestrates scenario selection,
/// spawn placement, grenade replay, and the in-game editor.
/// </summary>
public sealed class ExecuteMode : IGameMode
{
    public GameMode Mode => GameMode.Execute;

    // ── Sub-managers ───────────────────────────────────────────────────────────
    private readonly ScenarioManager _scenarioMgr = new();
    private readonly SpawnManager    _spawnMgr    = new();
    private readonly GrenadeManager  _grenadeMgr  = new();
    private ExecuteEditor?           _editor;

    // ── State ──────────────────────────────────────────────────────────────────
    private string  _mapName     = "";
    private string  _configPath  = "";
    private bool    _degraded    = false;   // true when no map config exists

    // ── Allocator / Superheroes delegate hooks (Track E / Track F) ────────────
    /// <summary>
    /// TODO (Track E): Set this delegate from the Allocator module to handle weapon allocation.
    /// Signature: (CCSPlayerController player) => void
    /// </summary>
    public static Action<CCSPlayerController>? OnAllocate;

    /// <summary>
    /// TODO (Track F): Set this delegate from the Superheroes module.
    /// Signature: (CCSPlayerController player) => void
    /// </summary>
    public static Action<CCSPlayerController>? OnSuperherosSpawn;

    // ── IGameMode ──────────────────────────────────────────────────────────────

    public Task OnEnterAsync(ModeContext ctx)
    {
        _mapName = ctx.MapName;

        LoadMapConfig();
        RegisterCommands();
        RegisterEvents();
        ApplyServerConfig();

        if (_degraded)
            Chat.BroadcastError($"[Execute] Aucune config pour '{_mapName}' — mode dégradé (T full util, pas de scénarios).");
        else
            Chat.Broadcast($"[Execute] Mode actif sur \x04{_mapName}\x01 — restart en cours.");

        // Force a clean round restart so spawns/scenarios apply immediately.
        Server.ExecuteCommand("mp_restartgame 1");

        CS2UltimodPlugin.Log?.LogInformation("[Execute] Entered. Map={Map} Degraded={Degraded}", _mapName, _degraded);
        return Task.CompletedTask;
    }

    public Task OnExitAsync(ModeContext ctx)
    {
        _grenadeMgr.CancelAll();
        _scenarioMgr.SetConfig(null);
        CS2UltimodPlugin.Log?.LogInformation("[Execute] Exited.");
        return Task.CompletedTask;
    }

    // ── Internal setup ────────────────────────────────────────────────────────

    private void LoadMapConfig()
    {
        // Configs live at: <ModuleDirectory>/../../../configs/maps/execute/<mapname>.json
        // We resolve via CS2UltimodPlugin.ModuleDirectory if available, else fall back to relative path.
        var baseDir = CS2UltimodPlugin.PluginDirectory.Length > 0
            ? CS2UltimodPlugin.PluginDirectory
            : AppContext.BaseDirectory;
        _configPath = Path.GetFullPath(Path.Combine(baseDir,
            "..", "..", "..", "configs", "maps", "execute", $"{_mapName}.json"));

        var cfg = ExecuteMapConfig.Load(_configPath);
        _degraded = cfg == null;
        _scenarioMgr.SetConfig(cfg);

        if (_degraded)
            CS2UltimodPlugin.Log?.LogInformation("[Execute] No config for '{Map}'. Degraded mode (retake spawns + full T util).", _mapName);
        else
            CS2UltimodPlugin.Log?.LogInformation("[Execute] Loaded {Count} scenario(s) for '{Map}'.", cfg!.Scenarios.Count, _mapName);

        // Build editor with closures over current config
        _editor = new ExecuteEditor(
            _scenarioMgr,
            _grenadeMgr,
            () => _configPath,
            () => _scenarioMgr.Config,
            newCfg => _scenarioMgr.SetConfig(newCfg));
    }

    private bool _eventsRegistered;

    public void RegisterEvents()
    {
        if (_eventsRegistered) return;
        _eventsRegistered = true;
        CS2UltimodPlugin.EventBus.Subscribe<MapStartEvent>(OnMapStart,       GameMode.Execute, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(OnRoundStart,   GameMode.Execute, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerSpawnEvent>(OnPlayerSpawn, GameMode.Execute, GameMode.Mixte);
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(OnRoundEnd,       GameMode.Execute, GameMode.Mixte);
    }

    private static bool _commandsRegistered;

    private void RegisterCommands()
    {
        // Guard: commands are global singletons — only register once across all mode-switches.
        // Retake's SpawnEditor already registers "addspawn", "removespawn", "showspawns", "hidespawns"
        // as aliases. Execute uses the same names. We register with try-catch to avoid collision exceptions.
        if (_commandsRegistered) return;
        _commandsRegistered = true;

        var modes = new[] { GameMode.Execute, GameMode.Mixte };

        RegisterSafe(new ChatCommand(
            "debug", null, "@cs2ultimod/edit",
            "Toggle edit mode",
            (p, a) => _editor?.OnDebug(p, a),
            modes));

        // Note: "addspawn", "removespawn", "showspawns", "hidespawns" may already be registered
        // by RetakeMode's SpawnEditor (as aliases for GameMode.Retake). RegisterSafe() silently
        // skips if a name collides. "exe*" prefixed versions are always registered as fallbacks.
        RegisterSafe(new ChatCommand(
            "addspawn", null, "@cs2ultimod/edit",
            "!addspawn <Name> <T|CT> — Add execute spawn at current position",
            (p, a) => _editor?.OnAddSpawn(p, a),
            modes));
        RegisterSafe(new ChatCommand(
            "exaddspawn", null, "@cs2ultimod/edit",
            "!exaddspawn <Name> <T|CT> — Add execute spawn (execute-mode fallback)",
            (p, a) => _editor?.OnAddSpawn(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "removespawn", null, "@cs2ultimod/edit",
            "!removespawn — Remove nearest execute spawn (menu)",
            (p, a) => _editor?.OnRemoveSpawn(p, a),
            modes));
        RegisterSafe(new ChatCommand(
            "exremovespawn", null, "@cs2ultimod/edit",
            "!exremovespawn — Remove nearest execute spawn (fallback)",
            (p, a) => _editor?.OnRemoveSpawn(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "listspawns", null, "@cs2ultimod/edit",
            "!listspawns — List execute spawns to console",
            (p, a) => _editor?.OnListSpawns(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "showspawns", null, "@cs2ultimod/edit",
            "!showspawns — Show execute spawn beams in-world",
            (p, a) => _editor?.OnShowSpawns(p, a),
            modes));
        RegisterSafe(new ChatCommand(
            "exshowspawns", null, "@cs2ultimod/edit",
            "!exshowspawns — Show execute spawn beams (fallback)",
            (p, a) => _editor?.OnShowSpawns(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "hidespawns", null, "@cs2ultimod/edit",
            "!hidespawns — Hide execute spawn beams",
            (p, a) => _editor?.OnHideSpawns(p, a),
            modes));
        RegisterSafe(new ChatCommand(
            "exhidespawns", null, "@cs2ultimod/edit",
            "!exhidespawns — Hide execute spawn beams (fallback)",
            (p, a) => _editor?.OnHideSpawns(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "createscenario", null, "@cs2ultimod/edit",
            "!createscenario <Name> <A|B> <MinPlayers>",
            (p, a) => _editor?.OnCreateScenario(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "addtspawntoscenario", null, "@cs2ultimod/edit",
            "!addtspawntoscenario — Add T-spawn to scenario (menu)",
            (p, a) => _editor?.OnAddTSpawnToScenario(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "addctspawntoscenario", null, "@cs2ultimod/edit",
            "!addctspawntoscenario — Add CT-spawn to scenario (menu)",
            (p, a) => _editor?.OnAddCtSpawnToScenario(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "addgrenadetoscenario", null, "@cs2ultimod/edit",
            "!addgrenadetoscenario — Add grenade to scenario (menu)",
            (p, a) => _editor?.OnAddGrenadeToScenario(p, a),
            modes));

        RegisterSafe(new ChatCommand(
            "runscenario", null, "@cs2ultimod/edit",
            "!runscenario — Replay all grenades for current scenario",
            (p, a) => _editor?.OnRunScenario(p, a),
            modes));
    }

    private static void RegisterSafe(ChatCommand cmd)
    {
        try
        {
            CS2UltimodPlugin.Commands.Register(cmd);
        }
        catch (InvalidOperationException)
        {
            // A command with this name was already registered (e.g., by another mode's editor).
            // Since AvailableInModes filters dispatch, this is logged but not fatal.
            CS2UltimodPlugin.Log?.LogWarning("[Execute] Command '{Cmd}' already registered — skipped.", cmd.Name);
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnMapStart(MapStartEvent evt)
    {
        _mapName = evt.MapName;
        LoadMapConfig();
        ApplyServerConfig();
    }

    private void OnRoundStart(RoundStartEvent evt)
    {
        // Re-asserter freezetime à 1 chaque round (cf. RetakeMode.OnRoundStart).
        Server.ExecuteCommand("mp_freezetime 1");

        if (CS2UltimodPlugin.ModeManager.Current == GameMode.Mixte && MixteMode.IsRetakeRound)
            return;
        if (_degraded)
        {
            // Degraded mode: spawn Ts and give them full util
            Server.NextFrame(() =>
            {
                var players = PlayerExt.InTeam(CsTeam.Terrorist).ToList();
                _spawnMgr.SetupDegradedMode(players);
                Chat.BroadcastError("[Execute] No map config — degraded mode (T side gets full util).");
            });
            return;
        }

        var players = Utilities.GetPlayers().Where(p => p.IsValid).ToList();
        var scenario = _scenarioMgr.PickRandom(players.Count);

        if (scenario == null)
        {
            Chat.BroadcastError("[Execute] No valid scenario found for this player count.");
            return;
        }

        // Configure bombsite visibility
        Server.NextFrame(() =>
        {
            SetBombsiteVisibility(scenario.Bombsite, scenario.DisableOtherBombsite);

            // Set round time via game rules
            try
            {
                var rules = GetGameRules();
                if (rules != null) rules.RoundTime = scenario.RoundTime;
            }
            catch { /* safe to ignore if game rules unavailable at this point */ }

            _spawnMgr.SetupSpawns(scenario);
            _grenadeMgr.SetupGrenades(scenario);

            // Announce scenario
            var desc = scenario.Description.Replace("{{site}}",
                scenario.Bombsite == EBombsite.Unknown ? "?" : scenario.Bombsite.ToString());
            Chat.Broadcast($"[Execute] Scenario: {scenario.Name} — {desc}");
        });
    }

    private void OnPlayerSpawn(PlayerSpawnEvent evt)
    {
        if (CS2UltimodPlugin.ModeManager.Current == GameMode.Mixte && MixteMode.IsRetakeRound)
            return;
        var player = evt.Player;
        if (!player.IsValid) return;

        // TODO (Track E — Allocator): Call weapon allocator if available.
        // When the allocator module (Track E) is implemented, it should set ExecuteMode.OnAllocate.
        // Example: OnAllocate?.Invoke(player);
        if (OnAllocate != null)
        {
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    OnAllocate.Invoke(player);
            });
        }

        // TODO (Track F — Superheroes): Call superheroes spawn hook if available.
        // Example: OnSuperherosSpawn?.Invoke(player);
        if (OnSuperherosSpawn != null)
        {
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    OnSuperherosSpawn.Invoke(player);
            });
        }
    }

    private void OnRoundEnd(RoundEndEvent evt)
    {
        // Log round outcome to DB asynchronously
        if (!string.IsNullOrEmpty(_mapName) && _scenarioMgr.CurrentScenario != null)
        {
            var mapName      = _mapName;
            var scenarioName = _scenarioMgr.CurrentScenario.Name;
            var winner       = (int)evt.Winner;

            _ = Task.Run(async () =>
            {
                try
                {
                    await CS2UltimodPlugin.Database.ExecuteAsync(
                        "INSERT INTO execute_round_stats (map, scenario, winner_team) VALUES (@Map, @Scenario, @Winner)",
                        new { Map = mapName, Scenario = scenarioName, Winner = winner });
                }
                catch (Exception ex)
                {
                    CS2UltimodPlugin.Log?.LogWarning("[Execute] DB log failed: {Err}", ex.Message);
                }
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ApplyServerConfig()
    {
        // Apply execute-specific cvars (override retake settings)
        Server.ExecuteCommand("bot_kick");
        Server.ExecuteCommand("bot_quota 0");
        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_plant_c4_anywhere 0");
        Server.ExecuteCommand("mp_freezetime 3");
        Server.ExecuteCommand("mp_roundtime_defuse 1.92");
        Server.ExecuteCommand("mp_c4timer 40");
        Server.ExecuteCommand("mp_maxmoney 0");
        Server.ExecuteCommand("mp_playercashawards 0");
        Server.ExecuteCommand("mp_teamcashawards 0");
        Server.ExecuteCommand("mp_halftime 0");
        Server.ExecuteCommand("mp_maxrounds 999");
        Server.ExecuteCommand("mp_round_restart_delay 1");
        Server.ExecuteCommand("mp_do_warmup_period 0");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
    }

    private static void SetBombsiteVisibility(EBombsite site, bool disableOther)
    {
        if (!disableOther) return;

        var targets = Utilities.FindAllEntitiesByDesignerName<CBombTarget>("func_bomb_target");
        foreach (var target in targets)
        {
            if (site == EBombsite.Unknown)
            {
                target.AcceptInput("Disable");
                continue;
            }

            bool isB    = target.IsBombSiteB;
            bool enable = site == EBombsite.B ? isB : !isB;
            target.AcceptInput(enable ? "Enable" : "Disable");
        }
    }

    private static CCSGameRules? GetGameRules()
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault();
        return proxy?.GameRules;
    }
}
