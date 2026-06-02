using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Ultimod.Features.Allocator;

public enum RoundType { Pistol, HalfBuy, FullBuy }

public enum RoundMode
{
    Sequential,       // pistol → half → full × N (both teams same)
    RandomSymmetric,  // random each round, both teams same
    RandomMixed,      // random per team each round
}

public sealed class RoundTypeManager
{
    private readonly Random _random = new();
    private int _roundsSincePistol;

    public RoundMode Mode { get; set; } = RoundMode.Sequential;
    public RoundType? Frozen { get; set; }

    private RoundType _tType = RoundType.Pistol;
    private RoundType _ctType = RoundType.Pistol;

    public void OnModeEnter()
    {
        _roundsSincePistol = 0;
        RollNext();
    }

    public void OnRoundEnd()
    {
        _roundsSincePistol++;
        RollNext();
    }

    public RoundType GetCurrentRoundType(CsTeam team = CsTeam.Terrorist)
    {
        if (Frozen.HasValue) return Frozen.Value;
        return team == CsTeam.CounterTerrorist ? _ctType : _tType;
    }

    public void ResetToPistol()
    {
        _roundsSincePistol = 0;
        _tType = _ctType = RoundType.Pistol;
    }

    private void RollNext()
    {
        if (Frozen.HasValue) return;

        switch (Mode)
        {
            case RoundMode.Sequential:
                var seq = _roundsSincePistol switch
                {
                    0 => RoundType.Pistol,
                    1 => RoundType.HalfBuy,
                    _ => RoundType.FullBuy,
                };
                _tType = _ctType = seq;
                break;
            case RoundMode.RandomSymmetric:
                var sym = RandomType();
                _tType = _ctType = sym;
                break;
            case RoundMode.RandomMixed:
                _tType = RandomType();
                _ctType = RandomType();
                break;
        }
    }

    private RoundType RandomType()
        => _random.Next(3) switch { 0 => RoundType.Pistol, 1 => RoundType.HalfBuy, _ => RoundType.FullBuy };
}
