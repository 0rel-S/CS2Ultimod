using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Utils;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Result of team formation: T-side list and CT-side list of steam IDs.
/// </summary>
public sealed class FormedTeams
{
    public List<ulong> TeamT  { get; set; } = [];
    public List<ulong> TeamCt { get; set; } = [];

    public CCSPlayerController? CaptainT  { get; set; }
    public CCSPlayerController? CaptainCt { get; set; }
}

/// <summary>
/// Implements the three team-formation sub-modes: Captain, Random, Elo.
/// </summary>
public sealed class TeamBuilder
{
    private readonly IMenuFramework _menus;
    private readonly FaceitClient _faceit;
    private readonly int _teamSize;

    // Captain mode state
    private readonly List<CCSPlayerController> _captains = [];
    private FormedTeams? _currentTeams;
    private List<CCSPlayerController> _remainingPicks = [];
    private int _pickTurn; // index into _captains (0 or 1)

    // Shuffle confirmation timer
    private System.Timers.Timer? _reshuffleTimer;

    /// <summary>Fires when teams are finalised and ready for map selection.</summary>
    public event Action<FormedTeams>? OnTeamsFormed;

    public TeamBuilder(IMenuFramework menus, FaceitClient faceit, int teamSize = 5)
    {
        _menus    = menus;
        _faceit   = faceit;
        _teamSize = teamSize;
    }

    // ── Captain mode ──────────────────────────────────────────────────────────

    public bool TryAddCaptain(CCSPlayerController player)
    {
        if (_captains.Any(c => c.SteamID == player.SteamID))
        {
            Chat.TellError(player, "Vous êtes déjà capitaine.");
            return false;
        }

        if (_captains.Count >= 2)
        {
            Chat.TellError(player, "Les 2 capitaines ont déjà été désignés.");
            return false;
        }

        _captains.Add(player);
        Chat.Broadcast($"⭐ {player.PlayerName} est maintenant capitaine #{_captains.Count}.");

        if (_captains.Count == 2)
        {
            Chat.Broadcast("Les 2 capitaines sont prêts. Début des picks dans 5s...");
            _ = Task.Delay(5000).ContinueWith(_ => Server.NextFrame(StartCaptainPicks));
        }

        return true;
    }

    public void ResetCaptains() => _captains.Clear();

    private void StartCaptainPicks()
    {
        var players = PlayerExt.AllConnected()
            .Where(p => !_captains.Any(c => c.SteamID == p.SteamID))
            .ToList();

        _remainingPicks = players;
        _currentTeams = new FormedTeams
        {
            CaptainT  = _captains[0],
            CaptainCt = _captains[1],
        };
        _currentTeams.TeamT.Add(_captains[0].SteamID);
        _currentTeams.TeamCt.Add(_captains[1].SteamID);
        _pickTurn = 0;

        DoNextCaptainPick();
    }

    private void DoNextCaptainPick()
    {
        if (_currentTeams == null) return;

        var tCount  = _currentTeams.TeamT.Count;
        var ctCount = _currentTeams.TeamCt.Count;

        if (tCount >= _teamSize && ctCount >= _teamSize)
        {
            AnnounceCaptainTeams();
            _ = Task.Delay(5000).ContinueWith(_ => Server.NextFrame(() => OnTeamsFormed?.Invoke(_currentTeams)));
            return;
        }

        // Alternate picks: cap1 (T) picks first
        var captain = _pickTurn == 0 ? _captains[0] : _captains[1];
        var pickingFor = _pickTurn == 0 ? "T" : "CT";
        var currentTeamSize = _pickTurn == 0 ? tCount : ctCount;

        if (currentTeamSize >= _teamSize)
        {
            _pickTurn = 1 - _pickTurn;
            DoNextCaptainPick();
            return;
        }

        Chat.Broadcast($"Tour de {captain.PlayerName} [{pickingFor}] — choisissez un joueur.");

        var available = _remainingPicks
            .Where(p => p.IsValid && p.Connected == PlayerConnectedState.Connected)
            .ToList();

        if (available.Count == 0)
        {
            AnnounceCaptainTeams();
            _ = Task.Delay(5000).ContinueWith(_ => Server.NextFrame(() => OnTeamsFormed?.Invoke(_currentTeams)));
            return;
        }

        // If only 1 player left and both teams need 1 more, auto-assign
        if (available.Count == 1)
        {
            var last = available[0];
            _remainingPicks.Remove(last);
            if (tCount < _teamSize)
                _currentTeams.TeamT.Add(last.SteamID);
            else
                _currentTeams.TeamCt.Add(last.SteamID);

            AnnounceCaptainTeams();
            _ = Task.Delay(5000).ContinueWith(_ => Server.NextFrame(() => OnTeamsFormed?.Invoke(_currentTeams)));
            return;
        }

        var teams = _currentTeams;
        var menu = _menus.CreatePlayerPicker(
            $"Choisir un joueur pour {pickingFor}",
            (picker, picked) =>
            {
                if (picker.SteamID != captain.SteamID)
                {
                    Chat.TellError(picker, "Ce n'est pas votre tour.");
                    return;
                }

                if (!_remainingPicks.Any(p => p.SteamID == picked.SteamID))
                {
                    Chat.TellError(picker, "Ce joueur a déjà été choisi.");
                    return;
                }

                _remainingPicks.RemoveAll(p => p.SteamID == picked.SteamID);

                if (_pickTurn == 0)
                    teams.TeamT.Add(picked.SteamID);
                else
                    teams.TeamCt.Add(picked.SteamID);

                Chat.Broadcast($"{captain.PlayerName} a choisi {picked.PlayerName} [{pickingFor}].");
                _pickTurn = 1 - _pickTurn;
                DoNextCaptainPick();
            },
            filter: p => available.Any(a => a.SteamID == p.SteamID));

        menu.Open(captain);
    }

    private void AnnounceCaptainTeams()
    {
        if (_currentTeams == null) return;
        Chat.Broadcast("=== Équipes formées ===");
        var teamT  = ResolveNames(_currentTeams.TeamT);
        var teamCt = ResolveNames(_currentTeams.TeamCt);
        Chat.Broadcast($"T  (cap: {_currentTeams.CaptainT?.PlayerName}): {string.Join(", ", teamT)}");
        Chat.Broadcast($"CT (cap: {_currentTeams.CaptainCt?.PlayerName}): {string.Join(", ", teamCt)}");
    }

    // ── Random mode ───────────────────────────────────────────────────────────

    public void StartRandomTeams(CCSPlayerController initiator)
    {
        var players = PlayerExt.AllConnected().ToList();
        if (players.Count < _teamSize * 2)
        {
            Chat.TellError(initiator, $"Nombre insuffisant de joueurs ({players.Count}/{_teamSize * 2}).");
            return;
        }

        var shuffled = Shuffle(players);
        var teams = BuildTeamsFromList(shuffled);
        _currentTeams = teams;
        AnnounceRandomTeams(teams);

        Chat.Broadcast("Utilisez !reshuffle dans les 30s pour mélanger à nouveau. Sinon les équipes sont confirmées.");

        _reshuffleTimer?.Dispose();
        _reshuffleTimer = new System.Timers.Timer(30_000);
        _reshuffleTimer.AutoReset = false;
        _reshuffleTimer.Elapsed += (_, _) => Server.NextFrame(() =>
        {
            if (_currentTeams == teams) // not reshuffled
            {
                Chat.Broadcast("Équipes confirmées !");
                OnTeamsFormed?.Invoke(teams);
            }
        });
        _reshuffleTimer.Start();
    }

    public void Reshuffle(CCSPlayerController player)
    {
        if (_currentTeams == null)
        {
            Chat.TellError(player, "Aucune équipe aléatoire en cours.");
            return;
        }

        var players = PlayerExt.AllConnected().ToList();
        var shuffled = Shuffle(players);
        var teams = BuildTeamsFromList(shuffled);
        _currentTeams = teams;
        AnnounceRandomTeams(teams);
        Chat.Broadcast("Équipes mélangées à nouveau! 30s pour !reshuffle ou confirmation automatique.");

        // Reset timer
        _reshuffleTimer?.Dispose();
        _reshuffleTimer = new System.Timers.Timer(30_000);
        _reshuffleTimer.AutoReset = false;
        _reshuffleTimer.Elapsed += (_, _) => Server.NextFrame(() =>
        {
            Chat.Broadcast("Équipes confirmées !");
            OnTeamsFormed?.Invoke(teams);
        });
        _reshuffleTimer.Start();
    }

    private void AnnounceRandomTeams(FormedTeams teams)
    {
        Chat.Broadcast("=== Équipes aléatoires ===");
        Chat.Broadcast($"T:  {string.Join(", ", ResolveNames(teams.TeamT))}");
        Chat.Broadcast($"CT: {string.Join(", ", ResolveNames(teams.TeamCt))}");
    }

    // ── Elo mode ─────────────────────────────────────────────────────────────

    public async Task StartEloTeamsAsync(CCSPlayerController initiator)
    {
        var players = PlayerExt.AllConnected().ToList();
        if (players.Count < _teamSize * 2)
        {
            Chat.TellError(initiator, $"Nombre insuffisant de joueurs ({players.Count}/{_teamSize * 2}).");
            return;
        }

        // Load elo for all players
        var eloMap = new Dictionary<ulong, int>();
        var missing = new List<string>();

        foreach (var p in players)
        {
            var link = await _faceit.GetCachedAsync(p.SteamID.ToString());
            if (link == null)
                missing.Add(p.PlayerName);
            else
                eloMap[p.SteamID] = link.Elo;
        }

        if (missing.Count > 0)
        {
            Server.NextFrame(() =>
            {
                Chat.TellError(initiator,
                    $"Joueurs sans compte Faceit lié: {string.Join(", ", missing)}. Utilisez !faceit <username>.");
            });
            return;
        }

        // Balance teams by Elo using a greedy approach
        var sorted = players.OrderByDescending(p => eloMap.GetValueOrDefault(p.SteamID, 0)).ToList();
        var teams = BalanceTeamsByElo(sorted, eloMap);

        Server.NextFrame(() =>
        {
            _currentTeams = teams;
            AnnounceEloTeams(teams, eloMap);
            Chat.Broadcast("Équipes Elo confirmées dans 10s.");

            _ = Task.Delay(10_000).ContinueWith(_ => Server.NextFrame(() => OnTeamsFormed?.Invoke(teams)));
        });
    }

    private FormedTeams BalanceTeamsByElo(List<CCSPlayerController> players, Dictionary<ulong, int> elos)
    {
        // Greedy: assign players (sorted desc by elo) to the team with the lower running total
        int half = players.Count / 2;

        var currentT  = new List<int>();
        var currentCt = new List<int>();
        int sumT = 0, sumCt = 0;

        // Sorted desc by elo — assign each player to the team with lower total
        foreach (var p in players.OrderByDescending(p => elos.GetValueOrDefault(p.SteamID)))
        {
            if (currentT.Count < half && (currentCt.Count >= half || sumT <= sumCt))
            {
                currentT.Add(players.IndexOf(p));
                sumT += elos.GetValueOrDefault(p.SteamID);
            }
            else
            {
                currentCt.Add(players.IndexOf(p));
                sumCt += elos.GetValueOrDefault(p.SteamID);
            }
        }

        return new FormedTeams
        {
            TeamT  = currentT.Select(i => players[i].SteamID).ToList(),
            TeamCt = currentCt.Select(i => players[i].SteamID).ToList(),
        };
    }

    private void AnnounceEloTeams(FormedTeams teams, Dictionary<ulong, int> elos)
    {
        Chat.Broadcast("=== Équipes Elo Faceit ===");

        var tNames = teams.TeamT.Select(id =>
        {
            var p = PlayerExt.FindBySteamId(id);
            return $"{p?.PlayerName ?? id.ToString()} ({elos.GetValueOrDefault(id)})";
        });

        var ctNames = teams.TeamCt.Select(id =>
        {
            var p = PlayerExt.FindBySteamId(id);
            return $"{p?.PlayerName ?? id.ToString()} ({elos.GetValueOrDefault(id)})";
        });

        var tTotal  = teams.TeamT.Sum(id => elos.GetValueOrDefault(id));
        var ctTotal = teams.TeamCt.Sum(id => elos.GetValueOrDefault(id));

        Chat.Broadcast($"T  [Elo total: {tTotal}]: {string.Join(", ", tNames)}");
        Chat.Broadcast($"CT [Elo total: {ctTotal}]: {string.Join(", ", ctNames)}");
    }

    // ── Apply teams in-game ───────────────────────────────────────────────────

    /// <summary>
    /// Moves players to their assigned teams in-game.
    /// </summary>
    public void ApplyTeams(FormedTeams teams)
    {
        foreach (var id in teams.TeamT)
        {
            var p = PlayerExt.FindBySteamId(id);
            if (p != null && p.IsValid)
                p.ChangeTeam(CsTeam.Terrorist);
        }

        foreach (var id in teams.TeamCt)
        {
            var p = PlayerExt.FindBySteamId(id);
            if (p != null && p.IsValid)
                p.ChangeTeam(CsTeam.CounterTerrorist);
        }
    }

    /// <summary>Swaps T and CT teams.</summary>
    public void SwapTeams(FormedTeams teams)
    {
        (teams.TeamT, teams.TeamCt) = (teams.TeamCt, teams.TeamT);
        (teams.CaptainT, teams.CaptainCt) = (teams.CaptainCt, teams.CaptainT);
        ApplyTeams(teams);
        Chat.Broadcast("Équipes inversées (T ↔ CT).");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FormedTeams BuildTeamsFromList(List<CCSPlayerController> shuffled)
    {
        int half = shuffled.Count / 2;
        return new FormedTeams
        {
            TeamT  = shuffled.Take(half).Select(p => p.SteamID).ToList(),
            TeamCt = shuffled.Skip(half).Select(p => p.SteamID).ToList(),
        };
    }

    private static List<CCSPlayerController> Shuffle(List<CCSPlayerController> list)
    {
        var copy = new List<CCSPlayerController>(list);
        var rng  = new Random();
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    private static List<string> ResolveNames(List<ulong> steamIds)
        => steamIds.Select(id =>
        {
            var p = PlayerExt.FindBySteamId(id);
            return p?.PlayerName ?? id.ToString();
        }).ToList();

    public void Dispose()
    {
        _reshuffleTimer?.Stop();
        _reshuffleTimer?.Dispose();
    }
}
