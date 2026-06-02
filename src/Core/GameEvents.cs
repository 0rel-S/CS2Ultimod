using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Ultimod.Core;

// Internal events dispatched by the foundation and routed through ModeAwareEventBus.
// Tracks MUST use ModeAwareEventBus.Subscribe — never hook CSS events directly.

public abstract record UltimodEvent;

public record RoundStartEvent(int RoundNumber) : UltimodEvent;
public record RoundEndEvent(CsTeam Winner, int ScoreT, int ScoreCT) : UltimodEvent;
public record PlayerSpawnEvent(CCSPlayerController Player) : UltimodEvent;

public record PlayerDeathEvent(
    CCSPlayerController Victim,
    CCSPlayerController? Attacker,
    string Weapon,
    bool Headshot) : UltimodEvent;

public record PlayerHurtEvent(
    CCSPlayerController Victim,
    CCSPlayerController? Attacker,
    int DamageHealth,
    string Weapon) : UltimodEvent;

public record BombPlantedEvent(CCSPlayerController Planter) : UltimodEvent;
public record BombDefusedEvent(CCSPlayerController Defuser) : UltimodEvent;
public record BombBeginDefuseEvent(CCSPlayerController Defuser) : UltimodEvent;
public record MapStartEvent(string MapName) : UltimodEvent;

public record InfernoStartburnEvent(int EntityId, float X, float Y, float Z) : UltimodEvent;
public record InfernoExpireEvent(int EntityId) : UltimodEvent;
