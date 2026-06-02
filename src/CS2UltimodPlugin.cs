using System.Reflection;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Database;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Permissions;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Features.Admin;
using CS2Ultimod.Features.Allocator;
using CS2Ultimod.Features.DamageReport;
using CS2Ultimod.Features.Hints;
using CS2Ultimod.Features.Superheroes;
using CS2Ultimod.Features.Votes;
using CS2Ultimod.Modes.Arena;
using CS2Ultimod.Modes.Execute;
using CS2Ultimod.Modes.Mixte;
using CS2Ultimod.Modes.Pickup;
using CS2Ultimod.Modes.Retake;
using CS2Ultimod.Modes.Stuff;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CS2Ultimod;

[MinimumApiVersion(367)]
public sealed class CS2UltimodPlugin : BasePlugin
{
    public override string ModuleName => "CS2Ultimod";
    public override string ModuleVersion => "0.1.0-dev";
    public override string ModuleAuthor => "Aurel";
    public override string ModuleDescription => "Plugin CS2 unifié : retake, execute, mixte, stuff, pickup, superheroes, admin.";

    // Foundation services
    public static IDatabase Database { get; private set; } = null!;
    public static IModeManager ModeManager { get; private set; } = null!;
    public static IModeAwareEventBus EventBus { get; private set; } = null!;
    public static IMenuFramework Menus { get; private set; } = null!;
    public static IPermissionService Permissions { get; private set; } = null!;
    public static ICommandRegistry Commands { get; private set; } = null!;
    public static string PluginDirectory { get; private set; } = "";
    public static Microsoft.Extensions.Logging.ILogger? Log { get; private set; }
    public static CS2UltimodPlugin Instance { get; private set; } = null!;

    private MenuFramework _menuFramework = null!;
    private RetakeMode _retakeMode = null!;
    private StuffMode _stuffMode = null!;
    private ArenaMode _arenaMode = null!;
    private bool _roundActive;

    public override void Load(bool hotReload)
    {
        PreloadNativeSqlite();
        PluginDirectory = ModuleDirectory;
        Log = Logger;
        Instance = this;
        Logger.LogInformation("[CS2Ultimod] Loading v{Version}", ModuleVersion);

        var config = LoadConfig();
        var dbPath = Path.Combine(ModuleDirectory, config.DatabasePath);

        // ── Foundation services ─────────────────────────────────────────────
        var db = new SqliteDatabase(dbPath);
        Database = db;

        var modeManager = new ModeManager(Logger);
        ModeManager = modeManager;
        EventBus = new ModeAwareEventBus(modeManager);

        _menuFramework = new MenuFramework();
        Menus = _menuFramework;

        var perms = new PermissionService(db, Logger);
        Permissions = perms;

        var registry = new CommandRegistry();
        Commands = registry;

        // ── Register DB migrations (must happen before RunMigrationsAsync) ──
        // Note: AdminModule.Register() also calls DatabaseRegistry.Register(new AdminMigration()).
        // AllocatorMigration and PickupMigration are registered here so migrations are in version order.
        DatabaseRegistry.Register(new AllocatorMigration());
        DatabaseRegistry.Register(new PickupMigration());
        ExecuteBootstrap.Register();

        // ── Create and register game modes ──────────────────────────────────
        var spawnConfigDir = Path.Combine(ModuleDirectory, "..", "..", "..", "configs", "retake", "spawns");

        _retakeMode = new RetakeMode(Logger, spawnConfigDir);
        var executeMode = new ExecuteMode();
        var mixteMode = new MixteMode();
        _stuffMode = new StuffMode();
        var pickupMode = new PickupMode(ModuleDirectory);
        _arenaMode = new ArenaMode();

        modeManager.Register(_retakeMode);
        modeManager.Register(executeMode);
        modeManager.Register(mixteMode);
        modeManager.Register(_stuffMode);
        modeManager.Register(pickupMode);
        modeManager.Register(_arenaMode);

        // ── Register event handlers (order matters for Mixte sub-mode picker)
        mixteMode.RegisterSubmodePicker();   // must be first in Mixte handlers
        _retakeMode.RegisterEvents();
        executeMode.RegisterEvents();
        _stuffMode.RegisterEvents();
        _arenaMode.RegisterEvents();

        // ── Register feature modules ─────────────────────────────────────────
        // AdminModule.Register also queues AdminMigration and hooks say/say_team for gag enforcement.
        AdminModule.Register(Commands, EventBus, this);
        AllocatorModule.Register();
        VoteModule.Register();
        StuffModule.Register();
        SuperheroesModule.Register();
        DamageReportModule.Register();
        HintsModule.Register();
        // PickupMode registers its own chat commands in OnEnterAsync / UnregisterCommands in OnExitAsync.

        // ── Run DB migrations + reload perms ────────────────────────────────
        _ = Task.Run(async () =>
        {
            await DatabaseRegistry.RunMigrationsAsync(db, Logger);
            Server.NextFrame(() =>
            {
                _ = perms.ReloadAsync();
                Logger.LogInformation("[CS2Ultimod] DB ready. Mode: {Mode}", ModeManager.Current);
            });
        });

        // ── Hook CSS events → event bus ─────────────────────────────────────
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Pre);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam, HookMode.Pre);
        RegisterEventHandler<EventInfernoStartburn>(OnInfernoStartburn);
        RegisterEventHandler<EventInfernoExpire>(OnInfernoExpire);
        RegisterListener<Listeners.OnTick>(_menuFramework.OnTick);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);

        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);

        // Console commands usable from RCON (no player required)
        AddCommand("ultimod_mode", "Change game mode (retake|execute|mixte|stuff|pickup|arena)", (player, info) =>
        {
            var modeStr = info.ArgCount > 1 ? info.GetArg(1).ToLower() : "";
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
            if (mode == null) { Logger.LogWarning("ultimod_mode: unknown mode '{M}'", modeStr); return; }
            var t = ModeManager.SwitchToAsync(mode.Value, reloadMap: false, reason: "rcon");
        });
        AddCommand("ultimod_reloadperms", "Reload admin permissions from DB", (player, info) =>
        {
            var t = Permissions.ReloadAsync();
        });
        AddCommand("ultimod_sh", "Superheroes admin (on|off|noob|pgm|rdm) — RCON callable", (player, info) =>
        {
            var sub = info.ArgCount > 1 ? info.GetArg(1) : "";
            if (string.IsNullOrEmpty(sub))
            {
                Logger.LogWarning("ultimod_sh: missing arg. Usage: ultimod_sh on|off|noob|pgm|rdm");
                return;
            }
            Features.Superheroes.SuperheroesModule.HandleRconAction(sub);
        });
        AddCommand("ultimod_sh_test", "Apply a specific hero to a player. Usage: ultimod_sh_test <heroId> <playerNamePartial>", (player, info) =>
        {
            if (info.ArgCount < 3)
            {
                Logger.LogWarning("ultimod_sh_test: usage: ultimod_sh_test <heroId> <playerNamePartial>");
                return;
            }
            Features.Superheroes.SuperheroesModule.HandleRconTest(info.GetArg(1), info.GetArg(2));
        });

        // ── Enter default mode ───────────────────────────────────────────────
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // let map fully load
            Server.NextFrame(() =>
                _ = modeManager.SwitchToAsync(GameMode.Retake, reloadMap: false, reason: "startup"));
        });

        Logger.LogInformation("[CS2Ultimod] Loaded. ModeManager default: {Mode}", ModeManager.Current);
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("[CS2Ultimod] Unloading");
        if (Database is IAsyncDisposable d)
            _ = d.DisposeAsync();
    }

    // ── CSS event → internal event bus ───────────────────────────────────────

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _roundActive = true;
        EventBus.Publish(new RoundStartEvent(0));
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundActive = false;
        if (ModeManager.IsActive(GameMode.Stuff))
            return HookResult.Stop;

        var winner = (CsTeam)@event.Winner;
        EventBus.Publish(new RoundEndEvent(winner, 0, 0));
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        // Arena réassigne les équipes chaque round (1 SwitchTeam/joueur × N arènes) :
        // on masque le spam "X a rejoint l'équipe Y" sans bloquer le changement.
        if (ModeManager.IsActive(GameMode.Arena))
        {
            info.DontBroadcast = true;
            return HookResult.Continue;
        }

        // Block T↔CT swap mid-round in retake-style modes — spawns are pre-assigned
        // and a swap leaves the player at a stale spawn (or on the wrong team).
        // Initial team picks (Spec → T/CT) are allowed.
        if (!_roundActive) return HookResult.Continue;
        if (!ModeManager.IsActive(GameMode.Retake) && !ModeManager.IsActive(GameMode.Mixte))
            return HookResult.Continue;

        var oldTeam = (CsTeam)@event.Oldteam;
        var newTeam = (CsTeam)@event.Team;
        var swapsBetweenPlayingTeams =
            (oldTeam == CsTeam.Terrorist || oldTeam == CsTeam.CounterTerrorist) &&
            (newTeam == CsTeam.Terrorist || newTeam == CsTeam.CounterTerrorist) &&
            oldTeam != newTeam;

        if (!swapsBetweenPlayingTeams) return HookResult.Continue;

        // Allow admin-driven swaps (caller is the issue here is only voluntary mid-round)
        if (@event.Disconnect) return HookResult.Continue;

        if (@event.Userid is { IsValid: true, IsBot: false } p)
            Chat.TellError(p, "Changement d'équipe interdit pendant le round.");
        return HookResult.Stop;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } p)
            EventBus.Publish(new PlayerSpawnEvent(p));
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim)
            EventBus.Publish(new PlayerDeathEvent(victim, @event.Attacker, @event.Weapon, @event.Headshot));
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim)
            EventBus.Publish(new PlayerHurtEvent(victim, @event.Attacker, @event.DmgHealth, @event.Weapon ?? ""));
        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } planter)
            EventBus.Publish(new BombPlantedEvent(planter));
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } defuser)
            EventBus.Publish(new BombDefusedEvent(defuser));
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } defuser)
            EventBus.Publish(new BombBeginDefuseEvent(defuser));
        return HookResult.Continue;
    }

    private void OnMapStart(string mapName)
        => EventBus.Publish(new MapStartEvent(mapName));

    private HookResult OnInfernoStartburn(EventInfernoStartburn @event, GameEventInfo info)
    {
        EventBus.Publish(new InfernoStartburnEvent(@event.Entityid, @event.X, @event.Y, @event.Z));
        return HookResult.Continue;
    }

    private HookResult OnInfernoExpire(EventInfernoExpire @event, GameEventInfo info)
    {
        EventBus.Publish(new InfernoExpireEvent(@event.Entityid));
        return HookResult.Continue;
    }

    // Quand un joueur arrive sur un serveur idle, CS2 peut être en warmup
    // (auto-warmup quand serveur vide). Pickup utilise warmup comme phase de
    // son state-machine, donc on n'y touche pas — partout ailleurs on dégage.
    private void OnClientPutInServer(int slot)
    {
        if (ModeManager.Current == GameMode.Pickup) return;
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        // mp_freezetime aime revenir à 15 (default) après les map-changes
        // ou les rechargements de cfg dathost. Force à 1 à chaque connect.
        Server.ExecuteCommand("mp_freezetime 1");
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is not { IsValid: true } p || p.IsBot) return HookResult.Continue;

        var steamId = p.SteamID;
        var name = p.PlayerName ?? "";

        // Log to admin_disconnected (used by !disconnected)
        _ = Database.ExecuteAsync(
            "INSERT INTO admin_disconnected (steam_id, name) VALUES (@SteamId, @Name)",
            new { SteamId = steamId.ToString(), Name = name });

        // Cleanup in-memory comm state so HashSets don't leak across sessions
        Features.Admin.AdminCommands.GaggedPlayers.Remove(steamId);
        Features.Admin.AdminCommands.MutedPlayers.Remove(steamId);

        return HookResult.Continue;
    }

    // ── Chat command dispatcher ───────────────────────────────────────────────

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;

        var text = info.GetArg(1).TrimStart('!', '.', '/');
        if (string.IsNullOrWhiteSpace(text)) return HookResult.Continue;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLower();
        var args = parts.Skip(1).ToArray();

        var reg = (CommandRegistry)Commands;
        if (!reg.TryResolve(name, ModeManager.Current, out var cmd) || cmd == null)
            return HookResult.Continue;

        if (cmd.RequiredFlag != null && !Permissions.RequireFlag(player, cmd.RequiredFlag))
            return HookResult.Handled;

        try { cmd.Handler(player, args); }
        catch (Exception ex) { Logger.LogError(ex, "[CommandRegistry] Error in handler for '{Cmd}'", name); }

        return HookResult.Handled;
    }

    // ── Native library preload (Linux) ───────────────────────────────────────
    // CSS loads plugins in isolation; the dotnet runtime can't find libe_sqlite3.so
    // from the plugin runtimes folder automatically. We load it explicitly first.
    private static void PreloadNativeSqlite()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var lib = Path.Combine(dir, "runtimes", "linux-x64", "native", "libe_sqlite3.so");
        if (File.Exists(lib))
            NativeLibrary.Load(lib);
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private PluginConfig LoadConfig()
    {
        var path = Path.Combine(ModuleDirectory, "..", "..", "..", "configs", "plugin.json");
        if (!File.Exists(path)) return new PluginConfig();
        try { return JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path)) ?? new PluginConfig(); }
        catch { return new PluginConfig(); }
    }
}

internal sealed class PluginConfig
{
    public string DefaultMode { get; set; } = "Retake";
    public string Language { get; set; } = "fr";
    public string DatabasePath { get; set; } = "data/plugin.db";
}
