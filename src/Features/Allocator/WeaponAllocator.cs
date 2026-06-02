using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core.Database;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Features.Allocator;

public sealed class WeaponAllocator
{
    private readonly IDatabase _db;
    private readonly Random _random = new();

    // AWP carriers du round courant (steam_id), tirés au hasard parmi les opt-in.
    // Tirés paresseusement à la 1ère allocation du round (EnsureCarriersForCurrentRound),
    // PAS à RoundEnd : à RoundEnd le GameManager fait des SwitchTeam (promotion/balance/
    // scramble), donc p.Team y est instable et ne correspond pas aux équipes du round où
    // l'AWP sera réellement donnée. On tire au spawn, quand les équipes sont définitives.
    private ulong? _awpCarrierT;
    private ulong? _awpCarrierCT;

    // _roundStamp avance à chaque RoundEnd (= dernier event avant les spawns du round
    // suivant). _carriersStamp retient le stamp pour lequel les carriers ont été tirés,
    // rendant EnsureCarriersForCurrentRound() idempotent sur tous les spawns d'un round.
    private int _roundStamp;
    private int _carriersStamp = -1;

    // Cache mémoire des opt-in "WantAwp" pour éviter une requête DB sur le main thread
    // au moment du tirage. Chargé au mode-enter, maintenu par SetWantAwpAsync.
    private readonly HashSet<ulong> _wantAwp = [];
    private readonly object _wantAwpLock = new();

    private static readonly Dictionary<CsTeam, Dictionary<RoundType, CsItem>> DefaultPrimaries = new()
    {
        [CsTeam.Terrorist] = new()
        {
            [RoundType.Pistol]  = CsItem.Glock,
            [RoundType.HalfBuy] = CsItem.Mac10,
            [RoundType.FullBuy] = CsItem.AK47,
        },
        [CsTeam.CounterTerrorist] = new()
        {
            [RoundType.Pistol]  = CsItem.USPS,
            [RoundType.HalfBuy] = CsItem.MP9,
            [RoundType.FullBuy] = CsItem.M4A1S,
        }
    };

    private static readonly Dictionary<CsTeam, CsItem> DefaultSecondary = new()
    {
        [CsTeam.Terrorist]       = CsItem.Deagle,
        [CsTeam.CounterTerrorist] = CsItem.Deagle,
    };

    public WeaponAllocator(IDatabase db) => _db = db;

    public async Task AllocateAsync(CCSPlayerController player, RoundType roundType)
    {
        if (!player.IsValid || player.IsBot) return;
        var team = player.Team;
        if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) return;

        var prefs = await GetPrefsAsync(player.SteamID, team);

        // CS2 entity APIs are not thread-safe — must run on main thread.
        Server.NextFrame(() =>
        {
            if (!player.IsValid) return;

            // Tirage des carriers AWP : idempotent par round, sur main thread (NextFrame
            // sérialisés → pas de race entre les allocations parallèles du round).
            EnsureCarriersForCurrentRound();

            player.RemoveWeapons();

            // Pistol: kevlar only (no helmet). HalfBuy/FullBuy: full armor.
            player.GiveNamedItem(roundType == RoundType.Pistol
                ? CsItem.Kevlar
                : CsItem.KevlarHelmet);

            if (team == CsTeam.CounterTerrorist)
            {
                var pawn = player.PlayerPawn.Value;
                if (pawn?.ItemServices != null)
                    new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser = true;
            }

            if (roundType != RoundType.Pistol)
            {
                var allocType = roundType == RoundType.HalfBuy ? "HalfBuyPrimary" : "FullBuyPrimary";
                var primary = prefs.TryGetValue(allocType, out var pref) ? pref : DefaultPrimaries[team][roundType];

                // Surcharge AWP : si full-buy + opt-in queue AWP + carrier du round
                // pour son équipe, on remplace son rifle par l'AWP. Un opt-in non
                // carrier garde sa pref rifle (pas de "downgrade").
                if (roundType == RoundType.FullBuy && prefs.ContainsKey("WantAwp"))
                {
                    var carrier = team == CsTeam.Terrorist ? _awpCarrierT : _awpCarrierCT;
                    bool isCarrier = carrier == player.SteamID;
                    if (isCarrier) primary = CsItem.AWP;
                    CS2UltimodPlugin.Log?.LogInformation(
                        "[AWP] {Player} (team={Team}, sid={Sid}) opt-in fullbuy → carrier={Carrier} → {Result}",
                        player.PlayerName, team, player.SteamID, carrier, isCarrier ? "AWP" : "rifle");
                }
                else if (roundType == RoundType.FullBuy)
                {
                    CS2UltimodPlugin.Log?.LogInformation(
                        "[AWP] {Player} (team={Team}, sid={Sid}) NOT opt-in for this team — keys: [{Keys}]",
                        player.PlayerName, team, player.SteamID, string.Join(",", prefs.Keys));
                }

                player.GiveNamedItem(primary);
            }

            var secondary = prefs.TryGetValue("Secondary", out var secPref) ? secPref : DefaultSecondary[team];
            player.GiveNamedItem(secondary);

            player.GiveNamedItem(CsItem.Knife);

            foreach (var nade in GetNadesForRound(team, roundType))
                player.GiveNamedItem(nade);
        });
    }

    // Aligné sur cs2-retakes-allocator : 1 nade random + 50% chance d'une 2e.
    // Évite le "toujours le même stuff" (smoke+flash+molly à chaque round) et
    // pousse à l'improvisation. Pistol garde sa limitation flash/smoke.
    private IEnumerable<CsItem> GetNadesForRound(CsTeam team, RoundType roundType)
    {
        if (roundType == RoundType.Pistol)
        {
            yield return _random.Next(2) == 0 ? CsItem.Flashbang : CsItem.Smoke;
            yield break;
        }

        var pool = new List<CsItem>
        {
            CsItem.Smoke, CsItem.Flashbang, CsItem.HE,
            team == CsTeam.Terrorist ? CsItem.Molotov : CsItem.Incendiary,
        };

        var first = pool[_random.Next(pool.Count)];
        yield return first;

        if (_random.NextDouble() >= 0.5) yield break;

        // 2e nade : pas de doublon sauf flash (autorisée à doubler comme dans la ref).
        if (first != CsItem.Flashbang) pool.Remove(first);
        yield return pool[_random.Next(pool.Count)];
    }

    public async Task<Dictionary<string, CsItem>> GetPrefsAsync(ulong steamId, CsTeam team)
    {
        var rows = await _db.QueryAsync<PrefRow>(
            "SELECT alloc_type, weapon FROM allocator_preferences WHERE steam_id = @SteamId AND team = @Team",
            new { SteamId = steamId.ToString(), Team = (int)team });

        var result = new Dictionary<string, CsItem>();
        foreach (var row in rows)
            if (Enum.TryParse<CsItem>(row.weapon, true, out var item))
                result[row.alloc_type] = item;
        return result;
    }

    public async Task SetPrefAsync(ulong steamId, CsTeam team, string allocType, CsItem weapon)
    {
        await _db.ExecuteAsync("""
            INSERT INTO allocator_preferences (steam_id, team, alloc_type, weapon)
            VALUES (@SteamId, @Team, @AllocType, @Weapon)
            ON CONFLICT(steam_id, team, alloc_type) DO UPDATE SET weapon=excluded.weapon
            """,
            new { SteamId = steamId.ToString(), Team = (int)team, AllocType = allocType, Weapon = weapon.ToString() });
    }

    public async Task RemovePrefAsync(ulong steamId, CsTeam team, string allocType)
    {
        await _db.ExecuteAsync(
            "DELETE FROM allocator_preferences WHERE steam_id=@SteamId AND team=@Team AND alloc_type=@AllocType",
            new { SteamId = steamId.ToString(), Team = (int)team, AllocType = allocType });
    }

    // Toggle WantAwp : opt-in GLOBAL (les 2 équipes). La rotation T↔CT est trop
    // fréquente pour qu'un opt-in côté T-seul soit utile (le joueur passe la moitié
    // de ses rounds en CT). On écrit dans les 2 lignes pour que GetPrefsAsync(team)
    // reconnaisse l'opt-in quel que soit le côté.
    public async Task SetWantAwpAsync(ulong steamId, CsTeam team, bool on)
    {
        if (on)
            await Task.WhenAll(
                SetPrefAsync(steamId, CsTeam.Terrorist,        "WantAwp", CsItem.AWP),
                SetPrefAsync(steamId, CsTeam.CounterTerrorist, "WantAwp", CsItem.AWP));
        else
            await Task.WhenAll(
                RemovePrefAsync(steamId, CsTeam.Terrorist,        "WantAwp"),
                RemovePrefAsync(steamId, CsTeam.CounterTerrorist, "WantAwp"));

        lock (_wantAwpLock)
        {
            if (on) _wantAwp.Add(steamId);
            else _wantAwp.Remove(steamId);
        }
    }

    // Charge le cache des opt-in WantAwp depuis la DB. À appeler au mode-enter.
    public async Task LoadWantAwpCacheAsync()
    {
        var ids = await _db.QueryAsync<string>(
            "SELECT DISTINCT steam_id FROM allocator_preferences WHERE alloc_type='WantAwp'");
        lock (_wantAwpLock)
        {
            _wantAwp.Clear();
            foreach (var id in ids)
                if (ulong.TryParse(id, out var sid)) _wantAwp.Add(sid);
        }
    }

    // Avance le round (appelé sur RoundEnd) : invalide les carriers du round écoulé.
    // Le tirage effectif est différé jusqu'à la 1ère allocation du round suivant.
    public void AdvanceRound() => _roundStamp++;

    // Tire 1 carrier au hasard par équipe parmi les opt-in présents dans l'équipe au
    // round courant. Idempotent : ne tire qu'une fois par round (gardé par _carriersStamp).
    // À appeler uniquement sur le main thread. Si personne n'a opt-in dans une équipe,
    // le carrier reste null → aucun AWP distribué de ce côté.
    private void EnsureCarriersForCurrentRound()
    {
        if (_carriersStamp == _roundStamp) return;
        _carriersStamp = _roundStamp;

        HashSet<ulong> want;
        lock (_wantAwpLock) { want = [.. _wantAwp]; }

        var poolT  = new List<ulong>();
        var poolCT = new List<ulong>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.IsBot || !want.Contains(p.SteamID)) continue;
            if (p.Team == CsTeam.Terrorist) poolT.Add(p.SteamID);
            else if (p.Team == CsTeam.CounterTerrorist) poolCT.Add(p.SteamID);
        }

        _awpCarrierT  = poolT .Count > 0 ? poolT [_random.Next(poolT .Count)] : null;
        _awpCarrierCT = poolCT.Count > 0 ? poolCT[_random.Next(poolCT.Count)] : null;

        CS2UltimodPlugin.Log?.LogInformation(
            "[AWP] Carriers stamp={Stamp}: poolT=[{PoolT}] poolCT=[{PoolCT}] → carrierT={CarrierT} carrierCT={CarrierCT}",
            _roundStamp, string.Join(",", poolT), string.Join(",", poolCT), _awpCarrierT, _awpCarrierCT);
    }

    public void ResetAwpCarriers()
    {
        _awpCarrierT = null;
        _awpCarrierCT = null;
        _roundStamp = 0;
        _carriersStamp = -1;
    }

    private sealed record PrefRow(string alloc_type, string weapon);
}
