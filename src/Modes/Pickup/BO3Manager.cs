using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Manages the post-match BO3 continuation vote.
/// </summary>
public sealed class BO3Manager : IDisposable
{
    private readonly IMenuFramework _menus;

    private readonly Dictionary<ulong, bool> _votes = [];
    private System.Timers.Timer? _voteTimer;
    private int _totalPlayers;

    /// <summary>Fires with true if majority voted yes, false otherwise.</summary>
    public event Action<bool>? OnVoteComplete;

    public BO3Manager(IMenuFramework menus)
    {
        _menus = menus;
    }

    /// <summary>
    /// Opens a 60-second BO3 vote for all connected players.
    /// </summary>
    public void StartVote()
    {
        _votes.Clear();
        var players = PlayerExt.AllConnected().ToList();
        _totalPlayers = players.Count;

        Chat.Broadcast("=== Vote BO3 ===");
        Chat.Broadcast("Continuer en BO3 avec les mêmes équipes ? Votez via le menu ou !bo3yes / !bo3no (60s).");

        foreach (var p in players)
            OpenVoteMenu(p);

        _voteTimer?.Dispose();
        _voteTimer = new System.Timers.Timer(60_000);
        _voteTimer.AutoReset = false;
        _voteTimer.Elapsed += (_, _) => Server.NextFrame(TallyVotes);
        _voteTimer.Start();
    }

    public void RegisterVote(CCSPlayerController player, bool yes)
    {
        if (_voteTimer == null) return; // vote not active

        _votes[player.SteamID] = yes;
        var yesCount = _votes.Count(v => v.Value);
        var noCount  = _votes.Count(v => !v.Value);
        Chat.TellSuccess(player, $"Vote enregistré: {(yes ? "Oui" : "Non")} ({yesCount} Oui / {noCount} Non).");

        // Early resolution if all have voted
        if (_votes.Count >= _totalPlayers)
            TallyVotes();
    }

    private void OpenVoteMenu(CCSPlayerController player)
    {
        var menu = _menus.Create("Continuer en BO3 ?");
        menu.AddItem("✔ Oui — jouer une autre carte", p => RegisterVote(p, true));
        menu.AddItem("✘ Non — retourner en Idle",     p => RegisterVote(p, false));
        menu.Open(player);
    }

    private void TallyVotes()
    {
        _voteTimer?.Stop();
        _voteTimer?.Dispose();
        _voteTimer = null;

        var yesCount = _votes.Count(v => v.Value);
        var noCount  = _votes.Count(v => !v.Value);

        Chat.Broadcast($"Résultat BO3: {yesCount} Oui / {noCount} Non.");
        var majority = yesCount > noCount;
        OnVoteComplete?.Invoke(majority);
    }

    public void Dispose()
    {
        _voteTimer?.Stop();
        _voteTimer?.Dispose();
    }
}
