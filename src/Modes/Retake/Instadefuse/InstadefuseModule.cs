using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using CsTeam = CounterStrikeSharp.API.Modules.Utils.CsTeam;

namespace CS2Ultimod.Modes.Retake.Instadefuse;

// Port of B3none/cs2-instadefuse — matched closely to avoid the routing/state issues we hit.
// Key invariants:
//   - _bombTicking is reset on RoundStart (NOT RoundEnd, which CS2 fires at halftime mid-round)
//   - _bombTicking is set true on bomb_planted (CSS event)
//   - All checks happen synchronously in OnBeginDefuse, mutations in NextFrame
public sealed class InstadefuseModule
{
    private float _bombPlantedTime = float.NaN;
    private bool _bombTicking;

    // Track active infernos by entity index — populated from inferno_startburn / inferno_expire events.
    // Keying by entityid avoids false positives from stale CInferno entities that linger after burnout
    // (which was causing the "instadefuse stays blocked after molotov ends" bug).
    private readonly Dictionary<int, (float X, float Y, float Z)> _activeInfernos = [];

    public void OnInfernoStartburn(InfernoStartburnEvent evt)
        => _activeInfernos[evt.EntityId] = (evt.X, evt.Y, evt.Z);

    public void OnInfernoExpire(InfernoExpireEvent evt)
        => _activeInfernos.Remove(evt.EntityId);

    public void OnRoundStart()
    {
        _bombPlantedTime = float.NaN;
        _bombTicking = false;
        _activeInfernos.Clear();
    }

    public void OnBombPlanted()
    {
        _bombPlantedTime = Server.CurrentTime;
        _bombTicking = true;
    }

    public void OnBeginDefuse(CCSPlayerController? defuser)
    {
        if (defuser == null || !defuser.IsValid || !defuser.PawnIsAlive) return;

        if (!_bombTicking) return;

        var planted = FindPlantedBomb();
        if (planted == null) return;
        if (planted.CannotBeDefused) return;

        if (TeamHasAlivePlayers(CsTeam.Terrorist)) return;

        // Active inferno (molotov/incendiary) near the bomb would interrupt the defuse animation.
        if (HasActiveInfernoNear(planted))
        {
            Chat.Broadcast($"Pas d'instadefuse pour {defuser.PlayerName} : feu actif sur le bombsite.");
            return;
        }

        var timeUntilDetonation = planted.TimerLength - (Server.CurrentTime - _bombPlantedTime);

        var defuseLength = planted.DefuseLength;
        if (defuseLength != 5f && defuseLength != 10f)
            defuseLength = defuser.PawnHasDefuser ? 5f : 10f;

        var canDefuseInTime = timeUntilDetonation - defuseLength >= 0f;

        if (!canDefuseInTime)
        {
            Chat.Broadcast($"Pas le temps pour {defuser.PlayerName} ! Il manque {Math.Abs(timeUntilDetonation - defuseLength):F1}s");
            Server.NextFrame(() =>
            {
                var bomb = FindPlantedBomb();
                if (bomb != null) bomb.C4Blow = 1.0f;
            });
            return;
        }

        Server.NextFrame(() =>
        {
            var bomb = FindPlantedBomb();
            if (bomb == null) return;
            bomb.DefuseCountDown = 0;
            Chat.Broadcast($"Instadefuse par {defuser.PlayerName} ! ({timeUntilDetonation:F1}s restantes)");
        });
    }

    private static bool TeamHasAlivePlayers(CsTeam team)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid) continue;
            if (p.Team != team) continue;
            if (!p.PawnIsAlive) continue;
            return true;
        }
        return false;
    }

    private static CPlantedC4? FindPlantedBomb()
    {
        var list = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").ToList();
        return list.FirstOrDefault();
    }

    private bool HasActiveInfernoNear(CPlantedC4 bomb)
    {
        // Inferno radius is ~150u; we use 250u to cover the entire defuse hitbox area.
        const float radiusSq = 250f * 250f;
        var bombPos = bomb.AbsOrigin;
        if (bombPos == null) return false;

        foreach (var (_, pos) in _activeInfernos)
        {
            var dx = pos.X - bombPos.X;
            var dy = pos.Y - bombPos.Y;
            var dz = pos.Z - bombPos.Z;
            if (dx * dx + dy * dy + dz * dz <= radiusSq) return true;
        }
        return false;
    }
}
