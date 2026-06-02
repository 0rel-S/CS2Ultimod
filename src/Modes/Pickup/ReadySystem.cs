using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Tracks !ready / !unready state for all connected players and renders the HUD.
/// </summary>
public sealed class ReadySystem : IDisposable
{
    private readonly PickupStateMachine _state;
    private readonly int _requiredCount;

    // SteamID64 → ready flag
    private readonly Dictionary<ulong, bool> _readyMap = [];

    private System.Timers.Timer? _hudTimer;
    private System.Timers.Timer? _readyCheckTimer;

    /// <summary>Fires when all required players are ready.</summary>
    public event Action? OnAllReady;

    /// <summary>Fires when ready-check times out or is cancelled (e.g., !unready).</summary>
    public event Action? OnReadyCheckCancelled;

    public ReadySystem(PickupStateMachine state, int requiredCount = 10)
    {
        _state = state;
        _requiredCount = requiredCount;
    }

    public void StartHudUpdates()
    {
        _hudTimer?.Dispose();
        _hudTimer = new System.Timers.Timer(5000);
        _hudTimer.Elapsed += (_, _) => Server.NextFrame(() => RenderHud());
        _hudTimer.AutoReset = true;
        _hudTimer.Start();
    }

    public void StopHudUpdates()
    {
        _hudTimer?.Stop();
        _hudTimer?.Dispose();
        _hudTimer = null;
    }

    public void SetReady(CCSPlayerController player, bool ready)
    {
        if (!player.IsValid) return;
        _readyMap[player.SteamID] = ready;
        CheckReadyStatus();
    }

    public void ForceAllReady()
    {
        foreach (var p in PlayerExt.AllConnected())
            _readyMap[p.SteamID] = true;
        CheckReadyStatus();
    }

    public bool IsReady(ulong steamId) => _readyMap.TryGetValue(steamId, out var r) && r;
    public bool IsReady(CCSPlayerController player) => IsReady(player.SteamID);

    public int ReadyCount()
    {
        var connected = PlayerExt.AllConnected().Select(p => p.SteamID).ToHashSet();
        return _readyMap.Count(kv => kv.Value && connected.Contains(kv.Key));
    }

    public int ConnectedCount() => PlayerExt.AllConnected().Count();

    public void Reset()
    {
        _readyMap.Clear();
        CancelReadyCheckTimer();
    }

    private void CheckReadyStatus()
    {
        var connectedPlayers = PlayerExt.AllConnected().ToList();
        if (connectedPlayers.Count < _requiredCount) return;

        var readyCount = connectedPlayers.Count(p => IsReady(p.SteamID));

        if (readyCount >= _requiredCount)
        {
            if (_state.Is(PickupPhase.Warmup))
            {
                // Transition to ReadyCheck and start countdown
                if (_state.TryTransition(PickupPhase.ReadyCheck))
                    StartReadyCheckTimer();
            }
        }
        else
        {
            // If we're in ReadyCheck and someone un-readied, cancel
            if (_state.Is(PickupPhase.ReadyCheck))
                CancelReadyCheck();
        }
    }

    private void StartReadyCheckTimer()
    {
        Chat.Broadcast("Match commence dans 10s — utilisez !unready pour annuler.");
        _readyCheckTimer?.Dispose();
        _readyCheckTimer = new System.Timers.Timer(10_000);
        _readyCheckTimer.AutoReset = false;
        _readyCheckTimer.Elapsed += (_, _) => Server.NextFrame(() => OnAllReady?.Invoke());
        _readyCheckTimer.Start();
    }

    private void CancelReadyCheck()
    {
        CancelReadyCheckTimer();
        _state.TryTransition(PickupPhase.Warmup);
        Chat.Broadcast("Vérification annulée — un joueur n'est plus prêt.");
        OnReadyCheckCancelled?.Invoke();
    }

    private void CancelReadyCheckTimer()
    {
        _readyCheckTimer?.Stop();
        _readyCheckTimer?.Dispose();
        _readyCheckTimer = null;
    }

    public void RenderHud(TeamFormationMode formation = TeamFormationMode.None)
    {
        var players = PlayerExt.AllConnected().ToList();
        var modeName = formation switch
        {
            TeamFormationMode.Captain => "Capitaine",
            TeamFormationMode.Random  => "Aléatoire",
            TeamFormationMode.Elo     => "Elo Faceit",
            _                         => "Inconnu"
        };

        var readyCount = players.Count(p => IsReady(p.SteamID));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<b>PICKUP — Mode: {modeName}</b>");
        sb.AppendLine($"Prêts: {readyCount}/{_requiredCount}  |  !ready pour confirmer");

        // Show players in 2 columns (left: 1-5, right: 6-10)
        int half = (players.Count + 1) / 2;
        for (int i = 0; i < half; i++)
        {
            var left  = players.ElementAtOrDefault(i);
            var right = players.ElementAtOrDefault(i + half);

            var leftStr  = left  != null ? FormatPlayer(left)  : "";
            var rightStr = right != null ? FormatPlayer(right) : "";

            sb.AppendLine($"{leftStr,-30}{rightStr}");
        }

        var html = sb.ToString();
        foreach (var p in players)
            Chat.HudCenter(p, html, 6f);
    }

    private string FormatPlayer(CCSPlayerController p)
    {
        var tick = IsReady(p.SteamID) ? "✓" : "✗";
        var color = IsReady(p.SteamID) ? "#00FF00" : "#FF4444";
        var name = System.Net.WebUtility.HtmlEncode(
            p.PlayerName.Length > 15 ? p.PlayerName[..15] + "…" : p.PlayerName);
        return $"<font color='{color}'>{name} {tick}</font>";
    }

    public void Dispose()
    {
        StopHudUpdates();
        CancelReadyCheckTimer();
    }
}
