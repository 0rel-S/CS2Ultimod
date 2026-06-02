using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Manages tactical pauses (mp_pause_match / mp_unpause_match).
/// Each team has PauseLimit pauses per half. Pauses auto-expire after 5 minutes.
/// </summary>
public sealed class PauseSystem : IDisposable
{
    private readonly int _pauseLimit;

    // Pauses used this half, per team
    private readonly Dictionary<CsTeam, int> _pausesUsed = new()
    {
        [CsTeam.Terrorist]        = 0,
        [CsTeam.CounterTerrorist] = 0,
    };

    // Which teams have requested unpause
    private readonly HashSet<CsTeam> _unpauseRequests = [];

    private bool _paused;
    private CsTeam? _pausedByTeam;
    private System.Timers.Timer? _autoUnpauseTimer;

    public bool IsPaused => _paused;

    public PauseSystem(int pauseLimit = 1)
    {
        _pauseLimit = pauseLimit;
    }

    public void ResetForHalf()
    {
        _pausesUsed[CsTeam.Terrorist]        = 0;
        _pausesUsed[CsTeam.CounterTerrorist] = 0;
        _unpauseRequests.Clear();
        _paused       = false;
        _pausedByTeam = null;
        _autoUnpauseTimer?.Stop();
        _autoUnpauseTimer?.Dispose();
        _autoUnpauseTimer = null;
    }

    public bool TryPause(CCSPlayerController player)
    {
        if (_paused)
        {
            Chat.TellError(player, "Le match est déjà en pause.");
            return false;
        }

        var team = player.Team;
        if (team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            Chat.TellError(player, "Seuls les joueurs en jeu peuvent demander une pause.");
            return false;
        }

        var used = _pausesUsed.GetValueOrDefault(team, 0);
        if (used >= _pauseLimit)
        {
            Chat.TellError(player, $"Votre équipe n'a plus de pause disponible ({_pauseLimit}/{_pauseLimit}).");
            return false;
        }

        _paused = true;
        _pausedByTeam = team;
        _pausesUsed[team] = used + 1;
        _unpauseRequests.Clear();

        Server.ExecuteCommand("mp_pause_match");
        var teamName = team == CsTeam.Terrorist ? "T" : "CT";
        Chat.Broadcast($"⏸ Pause demandée par {player.PlayerName} [{teamName}]. !unpause pour reprendre.");

        // Auto-unpause after 5 minutes
        _autoUnpauseTimer?.Dispose();
        _autoUnpauseTimer = new System.Timers.Timer(5 * 60 * 1000);
        _autoUnpauseTimer.AutoReset = false;
        _autoUnpauseTimer.Elapsed += (_, _) =>
            Server.NextFrame(() =>
            {
                if (_paused)
                {
                    DoUnpause(null);
                    Chat.Broadcast("⏸ Pause expirée automatiquement après 5 minutes.");
                }
            });
        _autoUnpauseTimer.Start();

        return true;
    }

    /// <summary>
    /// Records an unpause request from a player/admin.
    /// If both teams have agreed (or player has @css/generic), unpause immediately.
    /// </summary>
    public bool TryUnpause(CCSPlayerController player, bool isAdmin)
    {
        if (!_paused)
        {
            Chat.TellError(player, "Le match n'est pas en pause.");
            return false;
        }

        if (isAdmin)
        {
            DoUnpause(player);
            return true;
        }

        var team = player.Team;
        if (team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            Chat.TellError(player, "Seuls les joueurs en jeu peuvent reprendre.");
            return false;
        }

        _unpauseRequests.Add(team);
        Chat.Broadcast($"⏵ {player.PlayerName} veut reprendre. ({_unpauseRequests.Count}/2 équipes d'accord)");

        if (_unpauseRequests.Contains(CsTeam.Terrorist) && _unpauseRequests.Contains(CsTeam.CounterTerrorist))
        {
            DoUnpause(player);
            return true;
        }

        return false;
    }

    private void DoUnpause(CCSPlayerController? byPlayer)
    {
        _paused = false;
        _pausedByTeam = null;
        _unpauseRequests.Clear();

        _autoUnpauseTimer?.Stop();
        _autoUnpauseTimer?.Dispose();
        _autoUnpauseTimer = null;

        Server.ExecuteCommand("mp_unpause_match");
        var who = byPlayer != null ? $" par {byPlayer.PlayerName}" : "";
        Chat.Broadcast($"▶ Match repris{who}.");
    }

    public void Dispose()
    {
        _autoUnpauseTimer?.Stop();
        _autoUnpauseTimer?.Dispose();
    }
}
