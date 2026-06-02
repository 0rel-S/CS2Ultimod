using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Modes.Stuff;

public sealed class StuffMode : IGameMode
{
    public GameMode Mode => GameMode.Stuff;

    private readonly Dictionary<int, (Vector Pos, QAngle Ang, float Time)> _deathPositions = new();
    private const float DeathPositionTtlSec = 30f;
    private bool _eventsRegistered;

    public void RegisterEvents()
    {
        if (_eventsRegistered) return;
        _eventsRegistered = true;
        CS2UltimodPlugin.EventBus.Subscribe<PlayerDeathEvent>(OnPlayerDeath, GameMode.Stuff);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerSpawnEvent>(OnPlayerSpawn, GameMode.Stuff);
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(OnRoundStart, GameMode.Stuff);
        // Re-asserter les cvars stuff sur tout map change : dathost recharge
        // server.cfg/autoexec à chaque changelevel et écrase nos sv_cheats, etc.
        // Le MapStart fire AVANT que dathost exécute son cfg, donc on doit attendre
        // ~3s pour passer après lui.
        CS2UltimodPlugin.EventBus.Subscribe<MapStartEvent>(_ =>
        {
            new CounterStrikeSharp.API.Modules.Timers.Timer(3.0f, ApplyStuffCvars);
        }, GameMode.Stuff);
    }

    public Task OnEnterAsync(ModeContext ctx)
    {
        CS2UltimodPlugin.Log?.LogInformation("[Stuff] OnEnterAsync — applying cvars");
        RegisterEvents();
        _deathPositions.Clear();
        ApplyStuffCvars();
        Server.ExecuteCommand("mp_restartgame 1");
        return Task.CompletedTask;
    }

    private static void ApplyStuffCvars()
    {
        CS2UltimodPlugin.Log?.LogInformation("[Stuff] Applying cvars");
        Server.ExecuteCommand("sv_cheats 1");
        Server.ExecuteCommand("mp_friendlyfire 0");
        Server.ExecuteCommand("mp_freezetime 0");
        Server.ExecuteCommand("mp_roundtime 60");
        Server.ExecuteCommand("mp_roundtime_defuse 60");
        Server.ExecuteCommand("mp_buy_anywhere 1");
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_startmoney 65535");
        Server.ExecuteCommand("mp_maxmoney 65535");
        Server.ExecuteCommand("mp_respawnwavetime_ct 0");
        Server.ExecuteCommand("mp_respawnwavetime_t 0");
        Server.ExecuteCommand("mp_respawn_on_death_ct 1");
        Server.ExecuteCommand("mp_respawn_on_death_t 1");
        Server.ExecuteCommand("sv_infinite_ammo 1");
        Server.ExecuteCommand("mp_death_drop_gun 0");
        Server.ExecuteCommand("mp_death_drop_grenade 0");
        Server.ExecuteCommand("sv_grenade_trajectory_prac_pipreview 1");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
    }

    public Task OnExitAsync(ModeContext ctx)
    {
        _deathPositions.Clear();

        Server.ExecuteCommand("sv_infinite_ammo 0");
        Server.ExecuteCommand("sv_grenade_trajectory_prac_pipreview 0");
        Server.ExecuteCommand("mp_buy_anywhere 0");
        Server.ExecuteCommand("mp_give_player_c4 1");
        Server.ExecuteCommand("mp_startmoney 800");
        Server.ExecuteCommand("mp_maxmoney 16000");
        Server.ExecuteCommand("mp_respawnwavetime_ct 6");
        Server.ExecuteCommand("mp_respawnwavetime_t 6");
        Server.ExecuteCommand("mp_respawn_on_death_ct 0");
        Server.ExecuteCommand("mp_respawn_on_death_t 0");
        Server.ExecuteCommand("mp_death_drop_gun 1");
        Server.ExecuteCommand("mp_death_drop_grenade 1");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0");
        Server.ExecuteCommand("mp_freezetime 15");
        Server.ExecuteCommand("mp_roundtime 1.92");
        Server.ExecuteCommand("mp_roundtime_defuse 1.92");
        Server.ExecuteCommand("mp_restartgame 1");
        Server.ExecuteCommand("sv_cheats 0");
        return Task.CompletedTask;
    }

    private void OnPlayerDeath(PlayerDeathEvent evt)
    {
        var pawn = evt.Victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || pawn.AbsRotation == null) return;

        _deathPositions[evt.Victim.Slot] = (
            new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z),
            new QAngle(pawn.AbsRotation.X, pawn.AbsRotation.Y, pawn.AbsRotation.Z),
            Server.CurrentTime
        );
    }

    private void OnPlayerSpawn(PlayerSpawnEvent evt)
    {
        var player = evt.Player;
        if (!player.IsValid) return;

        Server.NextFrame(() =>
        {
            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            // Disable damage so a teammate's nade or self-explosion can't ruin a stuff session.
            pawn.TakesDamage = false;
            CounterStrikeSharp.API.Utilities.SetStateChanged(pawn, "CBaseEntity", "m_bTakesDamage");

            // Teleport to last death pos only if recent (else stale across sessions/map time).
            if (!_deathPositions.TryGetValue(player.Slot, out var saved)) return;
            if (Server.CurrentTime - saved.Time > DeathPositionTtlSec)
            {
                _deathPositions.Remove(player.Slot);
                return;
            }
            pawn.Teleport(saved.Pos, saved.Ang, new Vector(0, 0, 0));
        });
    }

    private void OnRoundStart(RoundStartEvent evt)
        => _deathPositions.Clear();
}
