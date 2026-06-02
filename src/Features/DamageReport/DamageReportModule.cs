using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core;

namespace CS2Ultimod.Features.DamageReport;

// Récap dégâts envoyé en chat à chaque joueur à la fin du round.
// Pour chaque adversaire avec lequel il a échangé : dmg infligés / hits, dmg reçus / hits, HP final.
public static class DamageReportModule
{
    private readonly record struct Pair(ulong Attacker, ulong Victim);

    private static readonly Dictionary<Pair, (int Damage, int Hits)> _entries = new();
    private static readonly Dictionary<ulong, string> _names = new();

    private static readonly GameMode[] _activeModes =
        { GameMode.Retake, GameMode.Execute, GameMode.Mixte, GameMode.Pickup };

    // Chat color codes (CS2)
    private const string CRESET   = "\x01";
    private const string CRED     = "\x07"; // light red — dmg dealt
    private const string CGREEN   = "\x04"; // green — alive HP
    private const string CGREY    = "\x08";
    private const string CYELLOW  = "\x09";
    private const string CLIGHTBL = "\x0B"; // light blue — dmg received
    private const string CDARKBL  = "\x0C";
    private const string CGOLD    = "\x10";

    public static void Register()
    {
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(_ => Clear(), _activeModes);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerHurtEvent>(OnHurt, _activeModes);
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(_ => SendReports(), _activeModes);
    }

    private static void Clear()
    {
        _entries.Clear();
        _names.Clear();
    }

    private static void OnHurt(PlayerHurtEvent e)
    {
        if (e.Attacker is not { IsValid: true } atk) return;
        if (atk.SteamID == e.Victim.SteamID) return;
        if (atk.TeamNum == e.Victim.TeamNum) return; // ignore TK / friendly fire
        if (e.DamageHealth <= 0) return;

        var key = new Pair(atk.SteamID, e.Victim.SteamID);
        var prev = _entries.TryGetValue(key, out var v) ? v : (0, 0);
        _entries[key] = (prev.Item1 + e.DamageHealth, prev.Item2 + 1);

        if (!string.IsNullOrEmpty(atk.PlayerName))      _names[atk.SteamID]      = atk.PlayerName;
        if (!string.IsNullOrEmpty(e.Victim.PlayerName)) _names[e.Victim.SteamID] = e.Victim.PlayerName;
    }

    private static void SendReports()
    {
        if (_entries.Count == 0) return;

        var players = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && !p.IsHLTV
                        && p.Connected == PlayerConnectedState.Connected)
            .ToList();

        // HP snapshot
        var hpById = new Dictionary<ulong, int>();
        foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid))
        {
            int hp = 0;
            var pawn = p.PlayerPawn?.Value;
            if (pawn != null && pawn.IsValid) hp = Math.Max(0, pawn.Health);
            hpById[p.SteamID] = hp;
            if (!_names.ContainsKey(p.SteamID) && !string.IsNullOrEmpty(p.PlayerName))
                _names[p.SteamID] = p.PlayerName;
        }

        foreach (var p in players)
            SendReportTo(p, hpById);
    }

    private static void SendReportTo(CCSPlayerController player, Dictionary<ulong, int> hpById)
    {
        var myId = player.SteamID;
        var perOpp = new Dictionary<ulong, (int OutDmg, int OutHits, int InDmg, int InHits)>();

        foreach (var (key, val) in _entries)
        {
            if (key.Attacker == myId)
            {
                var cur = perOpp.TryGetValue(key.Victim, out var c) ? c : default;
                cur.OutDmg += val.Damage; cur.OutHits += val.Hits;
                perOpp[key.Victim] = cur;
            }
            else if (key.Victim == myId)
            {
                var cur = perOpp.TryGetValue(key.Attacker, out var c) ? c : default;
                cur.InDmg += val.Damage; cur.InHits += val.Hits;
                perOpp[key.Attacker] = cur;
            }
        }

        if (perOpp.Count == 0) return;

        var ordered = perOpp
            .OrderByDescending(kv => kv.Value.OutDmg + kv.Value.InDmg)
            .ThenByDescending(kv => kv.Value.OutDmg)
            .ToList();

        // Header
        player.PrintToChat($" {CDARKBL}■ {CGOLD}Récap dégâts du round{CRESET}");

        foreach (var (oppId, s) in ordered)
        {
            var oppName = _names.TryGetValue(oppId, out var n) ? n : "?";
            var hp = hpById.TryGetValue(oppId, out var h) ? h : 0;

            string hpPart = hp > 0
                ? $"{CGREEN}{hp} hp{CRESET}"
                : $"{CRED}dead{CRESET}";

            // Format proche du screenshot, sans tiret cadratin ni flèches.
            var line =
                $" {CYELLOW}To{CRESET} [{CRED}{s.OutDmg}{CRESET} / {CGREY}{s.OutHits} hits{CRESET}] " +
                $"{CYELLOW}From{CRESET} [{CLIGHTBL}{s.InDmg}{CRESET} / {CGREY}{s.InHits} hits{CRESET}] " +
                $"{CRESET}{oppName} ({hpPart})";

            player.PrintToChat(line);
        }
    }
}
