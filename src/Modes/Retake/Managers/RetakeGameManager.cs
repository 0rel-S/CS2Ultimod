using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core.Utils;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Modes.Retake.Managers;

public sealed class RetakeGameManager
{
    private readonly Random _random = new();
    private readonly float _terroristRatio;
    private readonly int _maxPlayers;
    private readonly int _consecutiveWinsToScramble;

    private readonly HashSet<CCSPlayerController> _activePlayers = [];
    private readonly HashSet<CCSPlayerController> _queuePlayers = [];
    private int _consecutiveTWins;

    public IReadOnlySet<CCSPlayerController> ActivePlayers => _activePlayers;

    public RetakeGameManager(int maxPlayers = 9, float terroristRatio = 0.45f, int consecutiveWinsToScramble = 5)
    {
        _maxPlayers = maxPlayers;
        _terroristRatio = terroristRatio;
        _consecutiveWinsToScramble = consecutiveWinsToScramble;
    }

    public void PlayerConnected(CCSPlayerController player)
    {
        if (_activePlayers.Count < _maxPlayers)
            _activePlayers.Add(player);
        else
            _queuePlayers.Add(player);
    }

    public void PlayerDisconnected(CCSPlayerController player)
    {
        _activePlayers.Remove(player);
        _queuePlayers.Remove(player);
        PromoteFromQueue();
    }

    private void PromoteFromQueue()
    {
        while (_activePlayers.Count < _maxPlayers && _queuePlayers.Count > 0)
        {
            var next = _queuePlayers.First();
            _queuePlayers.Remove(next);
            _activePlayers.Add(next);
        }
    }

    // Rebuild _activePlayers depuis l'état serveur réel. PlayerConnected/Disconnected
    // n'étaient câblés à aucun event handler — _activePlayers restait vide en permanence,
    // donc PromoteCTSurvivorsToT et BalanceTeams iteraient sur 0 joueur (no-op silencieux).
    private void SyncActivePlayers()
    {
        _activePlayers.Clear();
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.IsBot) continue;
            if (p.Team != CsTeam.Terrorist && p.Team != CsTeam.CounterTerrorist) continue;
            if (_activePlayers.Count < _maxPlayers) _activePlayers.Add(p);
        }
    }

    public void OnRoundEnd(CsTeam winner)
    {
        SyncActivePlayers();

        // Pattern B3none/cs2-retakes :
        //  - T = récompense (camp attaquant en retake, plus facile à win)
        //  - CT win → on promeut les CT survivants en T ; les T deviennent CT
        //  - T win → aucun mouvement (juste compteur de win-streak)
        //  - 5 T wins d'affilée → scramble (top fraggers stackent côté T)
        // BalanceTeams() est TOUJOURS appelé en fin pour garantir la ratio 45/55,
        // même quand T win (sinon la composition dérive sans jamais se corriger).
        if (winner == CsTeam.Terrorist)
        {
            _consecutiveTWins++;
            if (_consecutiveTWins >= _consecutiveWinsToScramble)
            {
                ScrambleTeams();
                _consecutiveTWins = 0;
                return; // scramble assigne déjà la bonne ratio, pas besoin de re-balance
            }
        }
        else
        {
            _consecutiveTWins = 0;
            PromoteCTSurvivorsToT();
        }

        BalanceTeams();
    }

    // Fallback ratio-fix : déplace des joueurs entre équipes si la ratio dérive
    // (T wins en série, joueurs qui se connectent, etc.). Idempotent : noop si ratio OK.
    private void BalanceTeams()
    {
        var targetT = GetTargetTerroristCount();
        var currentT = _activePlayers.Count(p => p.IsValid && p.Team == CsTeam.Terrorist);

        if (currentT == targetT) return;

        var moved = new List<string>();
        if (currentT > targetT)
        {
            var toMove = _activePlayers
                .Where(p => p.IsValid && p.Team == CsTeam.Terrorist)
                .OrderBy(_ => _random.Next())
                .Take(currentT - targetT)
                .ToList();
            foreach (var p in toMove) { p.SwitchTeam(CsTeam.CounterTerrorist); moved.Add($"{p.PlayerName}→CT"); }
        }
        else
        {
            var toMove = _activePlayers
                .Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist)
                .OrderBy(_ => _random.Next())
                .Take(targetT - currentT)
                .ToList();
            foreach (var p in toMove) { p.SwitchTeam(CsTeam.Terrorist); moved.Add($"{p.PlayerName}→T"); }
        }
        CS2Ultimod.CS2UltimodPlugin.Log?.LogInformation(
            "[Retake] BalanceTeams active={N} targetT={Tgt} currentT={Cur} → moved [{Moved}]",
            _activePlayers.Count, targetT, currentT, string.Join(",", moved));
    }

    private void ScrambleTeams()
    {
        Chat.Broadcast("Scramble des équipes !");
        var players = _activePlayers.Where(p => p.IsValid).OrderBy(_ => _random.Next()).ToList();
        AssignTeams(players);
    }

    // CT win → les CT vivants (= ceux qui ont stoppé le retake = "top performers" de ce round)
    // sont promus T pour le round suivant. Les anciens T deviennent CT.
    // Si pas assez de CT survivants pour combler le quota T, on complète avec des CT random.
    private void PromoteCTSurvivorsToT()
    {
        var targetT = GetTargetTerroristCount();

        var ctSurvivors = _activePlayers
            .Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist && p.PawnIsAlive)
            .ToList();

        var newTerrorists = ctSurvivors.Take(targetT).ToList();

        if (newTerrorists.Count < targetT)
        {
            var fillers = _activePlayers
                .Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist)
                .Except(newTerrorists)
                .OrderBy(_ => _random.Next())
                .Take(targetT - newTerrorists.Count);
            newTerrorists.AddRange(fillers);
        }

        var tSet = new HashSet<CCSPlayerController>(newTerrorists);
        foreach (var p in _activePlayers.Where(p => p.IsValid))
        {
            var target = tSet.Contains(p) ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            if (p.Team != target) p.SwitchTeam(target);
        }
    }

    private void AssignTeams(List<CCSPlayerController> players)
    {
        var numT = Math.Max(1, (int)Math.Round(players.Count * _terroristRatio));
        for (var i = 0; i < players.Count; i++)
        {
            var target = i < numT ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            if (players[i].Team != target)
                players[i].SwitchTeam(target);
        }
    }

    public int GetTargetTerroristCount() =>
        Math.Max(1, (int)Math.Round(_activePlayers.Count * _terroristRatio));

    public void Reset()
    {
        _activePlayers.Clear();
        _queuePlayers.Clear();
        _consecutiveTWins = 0;
    }
}
