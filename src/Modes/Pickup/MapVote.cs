using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2Ultimod.Core.Menu;
using CS2Ultimod.Core.Utils;
using CS2Ultimod.Features.Votes;

namespace CS2Ultimod.Modes.Pickup;

/// <summary>
/// Handles map pick-ban (captain mode) and map vote (random/elo mode).
/// </summary>
public sealed class MapVote : IDisposable
{
    private readonly IMenuFramework _menus;
    private readonly List<string> _mapPool;

    // Pick-ban state (captain mode)
    private List<string> _remainingMaps = [];
    private readonly List<string> _bannedMaps  = [];
    private readonly List<string> _pickedMaps  = [];
    private int _pickBanStep; // 0=ct bans, 1=t bans, 2=ct picks, remaining=decider

    /// <summary>Fires with the chosen map name when the vote/pick-ban completes.</summary>
    public event Action<string>? OnMapChosen;

    public MapVote(IMenuFramework menus, List<string> mapPool)
    {
        _menus    = menus;
        _mapPool  = mapPool;
    }

    // ── Map Vote (random / elo mode) ──────────────────────────────────────────

    /// <summary>
    /// Map vote for all players, via the chat ballot (!vote &lt;n&gt;). No menu, nobody
    /// frozen. A random map is picked if nobody votes (the match needs a map).
    /// Winner announced after 30 seconds.
    /// </summary>
    public void StartMapVote()
    {
        if (_mapPool.Count == 0) return;
        if (_mapPool.Count == 1) { OnMapChosen?.Invoke(_mapPool[0]); return; }

        VoteModule.StartChatVote("pickupmap", "Vote carte (Pickup)", _mapPool,
            winner => OnMapChosen?.Invoke(winner), fallbackRandom: true);
    }

    // ── Pick-Ban (captain mode) ───────────────────────────────────────────────

    /// <summary>
    /// Starts the pick-ban sequence.
    /// Format: CT bans → T bans → CT picks → remaining map is decider.
    /// </summary>
    public void StartPickBan(CCSPlayerController captainCt, CCSPlayerController captainT)
    {
        _remainingMaps = [.._mapPool];
        _bannedMaps.Clear();
        _pickedMaps.Clear();
        _pickBanStep = 0;

        Chat.Broadcast("Début du pick-ban! Les capitaines choisissent via le menu.");
        DoPickBanStep(captainCt, captainT);
    }

    private void DoPickBanStep(CCSPlayerController captainCt, CCSPlayerController captainT)
    {
        // Step 0: CT bans
        // Step 1: T bans
        // Step 2: CT picks
        // After step 2: 1 map remains = decider

        if (_remainingMaps.Count == 1)
        {
            var decider = _remainingMaps[0];
            Chat.Broadcast($"Carte décisive: {decider}");
            OnMapChosen?.Invoke(decider);
            return;
        }

        string action;
        CCSPlayerController actor;

        switch (_pickBanStep)
        {
            case 0:
                action = "bannir";
                actor  = captainCt;
                break;
            case 1:
                action = "bannir";
                actor  = captainT;
                break;
            case 2:
                action = "choisir";
                actor  = captainCt;
                break;
            default:
                // Fallback: remaining = decider
                var decider = _remainingMaps[0];
                Chat.Broadcast($"Carte décisive: {decider}");
                OnMapChosen?.Invoke(decider);
                return;
        }

        var step = _pickBanStep;
        var menu = _menus.Create($"Pick-Ban — {actor.PlayerName}, {action} une carte");
        foreach (var map in _remainingMaps)
        {
            var m = map;
            menu.AddItem(m, p =>
            {
                if (p.SteamID != actor.SteamID)
                {
                    Chat.TellError(p, "Ce n'est pas votre tour.");
                    return;
                }

                _remainingMaps.Remove(m);

                if (step < 2)
                {
                    _bannedMaps.Add(m);
                    Chat.Broadcast($"❌ {p.PlayerName} a banni {m}. Cartes restantes: {string.Join(", ", _remainingMaps)}");
                }
                else
                {
                    _pickedMaps.Add(m);
                    Chat.Broadcast($"✅ {p.PlayerName} a choisi {m}!");
                }

                _pickBanStep++;
                DoPickBanStep(captainCt, captainT);
            });
        }

        menu.Open(actor);
        Chat.Tell(actor, $"Sélectionnez une carte à {action} dans le menu.");
    }

    // ── BO3 pick-ban (2 maps from remaining pool) ─────────────────────────────

    /// <summary>
    /// For BO3: each captain bans 1 map from the remaining pool,
    /// then the 2 remaining maps are used as BO3 maps.
    /// Calls <see cref="OnMapChosen"/> with the first selected map.
    /// </summary>
    public void StartBo3PickBan(CCSPlayerController captainCt, CCSPlayerController captainT,
        List<string> playedMaps)
    {
        _remainingMaps = _mapPool.Except(playedMaps).ToList();

        if (_remainingMaps.Count <= 2)
        {
            var m = _remainingMaps.FirstOrDefault() ?? _mapPool.First();
            Chat.Broadcast($"BO3 — Prochaine carte: {m}");
            OnMapChosen?.Invoke(m);
            return;
        }

        _bannedMaps.Clear();
        _pickedMaps.Clear();
        _pickBanStep = 0;

        Chat.Broadcast("BO3 — Pick-ban des cartes! Les capitaines bannissent via le menu.");
        DoBo3BanStep(captainCt, captainT);
    }

    private void DoBo3BanStep(CCSPlayerController captainCt, CCSPlayerController captainT)
    {
        // Each captain bans 1 map; remaining 2 are the BO3 maps
        if (_bannedMaps.Count >= 2 || _remainingMaps.Count <= 2)
        {
            var nextMap = _remainingMaps.First();
            Chat.Broadcast($"BO3 — Cartes: {string.Join(", ", _remainingMaps)}. Prochaine carte: {nextMap}");
            OnMapChosen?.Invoke(nextMap);
            return;
        }

        var isCaptainCtTurn = _bannedMaps.Count == 0;
        var actor = isCaptainCtTurn ? captainCt : captainT;

        var menu = _menus.Create($"BO3 Ban — {actor.PlayerName}, bannir une carte");
        foreach (var map in _remainingMaps)
        {
            var m = map;
            menu.AddItem(m, p =>
            {
                if (p.SteamID != actor.SteamID)
                {
                    Chat.TellError(p, "Ce n'est pas votre tour.");
                    return;
                }

                _remainingMaps.Remove(m);
                _bannedMaps.Add(m);
                Chat.Broadcast($"❌ {p.PlayerName} a banni {m}.");
                DoBo3BanStep(captainCt, captainT);
            });
        }

        menu.Open(actor);
        Chat.Tell(actor, "Sélectionnez une carte à bannir.");
    }

    public void Dispose()
    {
        // Plus de timer local : le vote carte est géré par VoteModule (bulletin chat).
    }
}
