using CounterStrikeSharp.API;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Modes.Mixte;

public sealed class MixteMode : IGameMode
{
    public GameMode Mode => GameMode.Mixte;

    public static bool IsRetakeRound { get; private set; } = true;

    private readonly Random _rng = new();
    private bool _subModePickerRegistered;

    // Must be called before retake/execute RegisterEvents() so the picker fires first.
    public void RegisterSubmodePicker()
    {
        if (_subModePickerRegistered) return;
        _subModePickerRegistered = true;
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(_ =>
        {
            IsRetakeRound = _rng.Next(2) == 0;
            Chat.Broadcast($"[Mixte] Round : {(IsRetakeRound ? "Retake" : "Execute")}");
        }, GameMode.Mixte);
    }

    public Task OnEnterAsync(ModeContext ctx)
    {
        Server.ExecuteCommand("mp_freezetime 2");
        Server.ExecuteCommand("mp_roundtime_defuse 0.25");
        Server.ExecuteCommand("mp_respawn_on_death_t 0");
        Server.ExecuteCommand("mp_respawn_on_death_ct 0");
        Server.ExecuteCommand("mp_free_armor 0");
        Server.ExecuteCommand("mp_buy_anywhere 0");

        Chat.Broadcast("[Mixte] Mode mixte actif — retake et execute en alternance aléatoire.");
        return Task.CompletedTask;
    }

    public Task OnExitAsync(ModeContext ctx) => Task.CompletedTask;
}
