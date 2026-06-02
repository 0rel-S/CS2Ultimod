namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Represents the current phase of a Pickup match.
/// Valid transitions:
///   Idle → Warmup → ReadyCheck → KnifeRound → KnifeVote → Live → HalfTime
///   → Live2 → MatchEnd → BO3Vote → Idle  (or map change for BO3)
/// </summary>
public enum PickupPhase
{
    Idle,
    Warmup,
    ReadyCheck,
    KnifeRound,
    KnifeVote,
    Live,
    HalfTime,
    Live2,
    MatchEnd,
    BO3Vote
}

/// <summary>
/// Sub-mode used to form teams.
/// </summary>
public enum TeamFormationMode
{
    None,
    Captain,
    Random,
    Elo
}

/// <summary>
/// Manages the Pickup state machine. All transitions are validated.
/// </summary>
public sealed class PickupStateMachine
{
    private static readonly Dictionary<PickupPhase, PickupPhase[]> ValidTransitions = new()
    {
        [PickupPhase.Idle]        = [PickupPhase.Warmup],
        [PickupPhase.Warmup]      = [PickupPhase.ReadyCheck, PickupPhase.Idle],
        [PickupPhase.ReadyCheck]  = [PickupPhase.KnifeRound, PickupPhase.Warmup],
        [PickupPhase.KnifeRound]  = [PickupPhase.KnifeVote, PickupPhase.Idle],
        [PickupPhase.KnifeVote]   = [PickupPhase.Live, PickupPhase.Idle],
        [PickupPhase.Live]        = [PickupPhase.HalfTime, PickupPhase.MatchEnd, PickupPhase.Idle],
        [PickupPhase.HalfTime]    = [PickupPhase.Live2, PickupPhase.Idle],
        [PickupPhase.Live2]       = [PickupPhase.MatchEnd, PickupPhase.Idle],
        [PickupPhase.MatchEnd]    = [PickupPhase.BO3Vote, PickupPhase.Idle],
        [PickupPhase.BO3Vote]     = [PickupPhase.Idle, PickupPhase.Warmup],
    };

    public PickupPhase Current { get; private set; } = PickupPhase.Idle;

    public event Action<PickupPhase, PickupPhase>? OnPhaseChanged;

    /// <summary>
    /// Attempts to transition to <paramref name="next"/>.
    /// Returns true on success, false if the transition is not valid.
    /// </summary>
    public bool TryTransition(PickupPhase next)
    {
        if (ValidTransitions.TryGetValue(Current, out var allowed) && allowed.Contains(next))
        {
            var previous = Current;
            Current = next;
            OnPhaseChanged?.Invoke(previous, next);
            return true;
        }
        return false;
    }

    /// <summary>Force-transitions without validation (use only for cleanup/exit).</summary>
    public void ForceIdle()
    {
        var previous = Current;
        Current = PickupPhase.Idle;
        if (previous != PickupPhase.Idle)
            OnPhaseChanged?.Invoke(previous, PickupPhase.Idle);
    }

    public bool Is(PickupPhase phase) => Current == phase;
    public bool IsLive() => Current is PickupPhase.Live or PickupPhase.HalfTime or PickupPhase.Live2;
}
