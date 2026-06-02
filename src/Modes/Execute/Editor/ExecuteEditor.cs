using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Modes.Execute.Managers;
using CS2Ultimod.Modes.Execute.Models;
using CS2Ultimod.Modes.Execute.Models.Enums;

namespace CS2Ultimod.Modes.Execute.Editor;

/// <summary>
/// Handles all in-game editor commands for the Execute mode.
/// Ported from bazookaCodes/cs2-executes-plugin.
/// </summary>
public sealed class ExecuteEditor
{
    private readonly ScenarioManager  _scenarios;
    private readonly GrenadeManager   _grenades;
    private readonly Func<string>     _configPathProvider;
    private readonly Func<ExecuteMapConfig?> _configProvider;
    private readonly Action<ExecuteMapConfig> _configSetter;

    // Tracks the last thrown grenade per player (slot → Grenade) for !addgrenadetoscenario
    private readonly Dictionary<int, Grenade> _lastGrenadeByPlayer = new();

    public bool InEditMode { get; private set; }

    public ExecuteEditor(
        ScenarioManager     scenarios,
        GrenadeManager      grenades,
        Func<string>        configPathProvider,
        Func<ExecuteMapConfig?> configProvider,
        Action<ExecuteMapConfig> configSetter)
    {
        _scenarios          = scenarios;
        _grenades           = grenades;
        _configPathProvider = configPathProvider;
        _configProvider     = configProvider;
        _configSetter       = configSetter;
    }

    // ── Called by Executes.cs OnEntitySpawnedHandler equivalent ───────────────

    /// <summary>
    /// Record a grenade projectile thrown in edit mode (mirrors bazookaCodes OnEntitySpawnedHandler).
    /// Called from ExecuteMode when a new grenade projectile is spawned during edit mode.
    /// </summary>
    public void RecordGrenadeThrown(CCSPlayerController player, Grenade g)
    {
        _lastGrenadeByPlayer[player.Slot] = g;
        Chat.Tell(player, $"[Edit] Nade recorded: {g}");
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    public void OnDebug(CCSPlayerController player, string[] args)
    {
        InEditMode = !InEditMode;
        if (InEditMode)
        {
            Server.ExecuteCommand("mp_warmup_start");
            Server.ExecuteCommand("mp_warmuptime 120");
            Server.ExecuteCommand("mp_warmup_pausetimer 1");
        }
        else
        {
            // Restore : sinon le serveur reste figé en warmup même après mode change.
            Server.ExecuteCommand("mp_warmup_pausetimer 0");
            Server.ExecuteCommand("mp_warmup_end");
        }
        Chat.Tell(player, $"[Execute] Edit mode: {(InEditMode ? "ON" : "OFF")}");
    }

    public void OnAddSpawn(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        if (args.Length < 2)
        {
            Chat.TellError(player, "Usage: !addspawn <Name> <T|CT>");
            return;
        }

        var name     = args[0];
        var teamArg  = args[1].ToUpperInvariant();

        CsTeam team;
        if (teamArg == "T")       team = CsTeam.Terrorist;
        else if (teamArg == "CT") team = CsTeam.CounterTerrorist;
        else
        {
            Chat.TellError(player, "Team must be T or CT.");
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
        {
            Chat.TellError(player, "You must be alive with a valid pawn.");
            return;
        }

        var cfg = EnsureConfig();

        // Proximity check (72 units)
        foreach (var s in cfg.Spawns)
        {
            if (s.Position == null) continue;
            var dist = Distance2D(s.Position, pawn.AbsOrigin);
            if (dist <= 72.0)
            {
                Chat.TellError(player, "Too close to another spawn. Move further away.");
                return;
            }
        }

        var spawn = new Spawn
        {
            Id       = Guid.NewGuid(),
            Name     = name,
            Position = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z),
            Angle    = pawn.AbsRotation != null
                           ? new QAngle(pawn.AbsRotation.X, pawn.AbsRotation.Y, pawn.AbsRotation.Z)
                           : new QAngle(0, 0, 0),
            Team     = team,
            Type     = ESpawnType.Normal
        };

        cfg.Spawns.Add(spawn);
        SaveAndReload(cfg);
        Chat.TellSuccess(player, $"Spawn '{name}' ({teamArg}) added.");
    }

    public void OnRemoveSpawn(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg  = _configProvider();
        if (cfg == null || cfg.Spawns.Count == 0)
        {
            Chat.TellError(player, "No spawns to remove.");
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
        {
            Chat.TellError(player, "You must be alive.");
            return;
        }

        // Build menu of nearby spawns (within 256 units)
        var nearby = cfg.Spawns
            .Where(s => s.Position != null && Distance2D(s.Position, pawn.AbsOrigin) <= 256.0)
            .OrderBy(s => Distance2D(s.Position!, pawn.AbsOrigin))
            .ToList();

        if (nearby.Count == 0)
        {
            Chat.TellError(player, "No spawns within 256 units.");
            return;
        }

        var menu = CS2UltimodPlugin.Menus.Create("Remove Spawn");
        foreach (var sp in nearby)
        {
            var spawn = sp;
            menu.AddItem($"{spawn.Name} ({spawn.Team})", p =>
            {
                cfg.Spawns.Remove(spawn);
                // Also remove from scenario spawn id lists
                foreach (var sc in cfg.Scenarios)
                    sc.SpawnIds.Remove(spawn.Id);
                SaveAndReload(cfg);
                Chat.TellSuccess(p, $"Spawn '{spawn.Name}' removed.");
            });
        }
        menu.Open(player);
    }

    public void OnListSpawns(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg = _configProvider();
        if (cfg == null)
        {
            Chat.TellError(player, "No config loaded.");
            return;
        }

        player.PrintToConsole($"[Execute] ---- Spawns ({cfg.Spawns.Count}) ----");
        foreach (var s in cfg.Spawns)
            player.PrintToConsole($"  [{s.Team}] {s.Name}  id:{s.Id}  pos:{s.Position}");

        Chat.Tell(player, $"{cfg.Spawns.Count} spawn(s) printed to console.");
    }

    public void OnShowSpawns(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg = _configProvider();
        if (cfg == null) { Chat.TellError(player, "No config loaded."); return; }

        int count = 0;
        foreach (var s in cfg.Spawns)
        {
            SpawnBeam(s);
            count++;
        }
        Chat.TellSuccess(player, $"Showing {count} spawn(s).");
    }

    public void OnHideSpawns(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg = _configProvider();
        if (cfg == null) { Chat.TellError(player, "No config loaded."); return; }

        int removed = 0;
        foreach (var s in cfg.Spawns)
            removed += RemoveBeamsAt(s.Position);

        Chat.TellSuccess(player, $"Removed {removed} spawn beam(s).");
    }

    public void OnCreateScenario(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        if (args.Length < 3)
        {
            Chat.TellError(player, "Usage: !createscenario <Name> <A|B|Unknown> <MinPlayers>");
            return;
        }

        var name       = args[0];
        var siteArg    = args[1].ToUpperInvariant();
        if (!int.TryParse(args[2], out var minPlayers)) minPlayers = 2;

        var site = siteArg switch
        {
            "A" => EBombsite.A,
            "B" => EBombsite.B,
            _   => EBombsite.Unknown
        };

        var scenario = new Scenario
        {
            Name           = name,
            Bombsite       = site,
            RoundTime      = 90,
            MinPlayerCount = minPlayers
        };

        var cfg = EnsureConfig();
        if (!_scenarios.AddScenario(scenario))
        {
            Chat.TellError(player, $"Scenario '{name}' already exists.");
            return;
        }
        SaveAndReload(cfg);
        Chat.TellSuccess(player, $"Scenario '{name}' created (site:{site}, minPlayers:{minPlayers}).");
    }

    public void OnAddTSpawnToScenario(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg = _configProvider();
        if (cfg == null || cfg.Scenarios.Count == 0)
        {
            Chat.TellError(player, "No scenarios available. Use !createscenario first.");
            return;
        }

        var tSpawns = cfg.Spawns.Where(s => s.Team == CsTeam.Terrorist).ToList();
        if (tSpawns.Count == 0)
        {
            Chat.TellError(player, "No T spawns available. Use !addspawn <Name> T first.");
            return;
        }

        var scenarioMenu = CS2UltimodPlugin.Menus.Create("Add T-Spawn → Scenario");
        foreach (var scenario in cfg.Scenarios)
        {
            var sc = scenario;
            var spawnMenu = CS2UltimodPlugin.Menus.Create($"T-Spawns for '{sc.Name}'");
            foreach (var sp in tSpawns)
            {
                var spawn = sp;
                spawnMenu.AddItem(spawn.Name, p =>
                {
                    _scenarios.AddSpawnToScenario(sc.Name, spawn.Id);
                    SaveAndReload(cfg);
                    Chat.TellSuccess(p, $"T-Spawn '{spawn.Name}' added to '{sc.Name}'.");
                });
            }
            scenarioMenu.AddSubmenu(sc.Name, spawnMenu);
        }
        scenarioMenu.Open(player);
    }

    public void OnAddCtSpawnToScenario(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg = _configProvider();
        if (cfg == null || cfg.Scenarios.Count == 0)
        {
            Chat.TellError(player, "No scenarios available.");
            return;
        }

        var ctSpawns = cfg.Spawns.Where(s => s.Team == CsTeam.CounterTerrorist).ToList();
        if (ctSpawns.Count == 0)
        {
            Chat.TellError(player, "No CT spawns available. Use !addspawn <Name> CT first.");
            return;
        }

        var scenarioMenu = CS2UltimodPlugin.Menus.Create("Add CT-Spawn → Scenario");
        foreach (var scenario in cfg.Scenarios)
        {
            var sc = scenario;
            var spawnMenu = CS2UltimodPlugin.Menus.Create($"CT-Spawns for '{sc.Name}'");
            foreach (var sp in ctSpawns)
            {
                var spawn = sp;
                spawnMenu.AddItem(spawn.Name, p =>
                {
                    _scenarios.AddSpawnToScenario(sc.Name, spawn.Id);
                    SaveAndReload(cfg);
                    Chat.TellSuccess(p, $"CT-Spawn '{spawn.Name}' added to '{sc.Name}'.");
                });
            }
            scenarioMenu.AddSubmenu(sc.Name, spawnMenu);
        }
        scenarioMenu.Open(player);
    }

    public void OnAddGrenadeToScenario(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var cfg = _configProvider();
        if (cfg == null || cfg.Scenarios.Count == 0)
        {
            Chat.TellError(player, "No scenarios available.");
            return;
        }

        if (cfg.Grenades.Count == 0)
        {
            Chat.TellError(player, "No grenades saved. Throw a nade first — it will be recorded.");
            return;
        }

        var scenarioMenu = CS2UltimodPlugin.Menus.Create("Add Grenade → Scenario");
        foreach (var scenario in cfg.Scenarios)
        {
            var sc = scenario;
            var grenMenu = CS2UltimodPlugin.Menus.Create($"Grenades for '{sc.Name}'");
            foreach (var gr in cfg.Grenades)
            {
                var grenade = gr;
                grenMenu.AddItem($"{grenade.Name} ({grenade.Type})", p =>
                {
                    _scenarios.AddGrenadeToScenario(sc.Name, grenade.Id);
                    SaveAndReload(cfg);
                    Chat.TellSuccess(p, $"Grenade '{grenade.Name}' added to '{sc.Name}'.");
                });
            }
            scenarioMenu.AddSubmenu(sc.Name, grenMenu);
        }
        scenarioMenu.Open(player);
    }

    public void OnRunScenario(CCSPlayerController player, string[] args)
    {
        if (!RequireEditMode(player)) return;

        var scenario = _scenarios.CurrentScenario;
        if (scenario == null)
        {
            Chat.TellError(player, "No current scenario selected. Use !forscenario or wait for round start.");
            return;
        }

        Chat.Tell(player, $"[Execute] Re-throwing all grenades for scenario '{scenario.Name}'...");
        _grenades.SetupGrenades(scenario);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool RequireEditMode(CCSPlayerController player)
    {
        if (!InEditMode)
        {
            Chat.TellError(player, "Command requires edit mode. Use !debug first.");
            return false;
        }
        return true;
    }

    private ExecuteMapConfig EnsureConfig()
    {
        var cfg = _configProvider();
        if (cfg == null)
        {
            cfg = new ExecuteMapConfig();
            _configSetter(cfg);
            _scenarios.SetConfig(cfg);
        }
        return cfg;
    }

    private void SaveAndReload(ExecuteMapConfig cfg)
    {
        cfg.ParseIdReferences();
        cfg.Save(_configPathProvider());
    }

    private static double Distance2D(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void SpawnBeam(Spawn spawn)
    {
        if (spawn.Position == null) return;

        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null) return;

        beam.StartFrame = 0;
        beam.FrameRate  = 0;
        beam.LifeState  = 1;
        beam.Width      = 5;
        beam.EndWidth   = 5;
        beam.Amplitude  = 0;
        beam.Speed      = 50;
        beam.Flags      = 0;
        beam.BeamType   = BeamType_t.BEAM_HOSE;
        beam.FadeLength = 10.0f;
        beam.Render     = spawn.Team == CsTeam.Terrorist
            ? Color.FromArgb(255, Color.Red)
            : Color.FromArgb(255, Color.Blue);
        beam.EndPos.X   = spawn.Position.X;
        beam.EndPos.Y   = spawn.Position.Y;
        beam.EndPos.Z   = spawn.Position.Z + 100.0f;
        beam.Teleport(spawn.Position, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        beam.DispatchSpawn();
    }

    private static int RemoveBeamsAt(Vector? pos)
    {
        if (pos == null) return 0;

        int count = 0;
        var beams = Utilities.FindAllEntitiesByDesignerName<CBeam>("beam");
        foreach (var b in beams)
        {
            if (b.AbsOrigin == null) continue;
            if (MathF.Abs(b.AbsOrigin.X - pos.X) < 1f &&
                MathF.Abs(b.AbsOrigin.Y - pos.Y) < 1f &&
                MathF.Abs(b.AbsOrigin.Z - pos.Z) < 1f)
            {
                b.Remove();
                count++;
            }
        }
        return count;
    }
}
