using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Ultimod.Core;
using CS2Ultimod.Core.Utils;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Features.Superheroes;

// Auto-attribution de héros selon perf round précédent.
// - Modes asymétriques (Retake/Execute/Mixte) : ranking global, split en quintiles → tiers -2..+2
// - Pickup (5v5) : ranking par équipe, positions 1..5 → tiers -2,-1,0,+1,+2
// - Round 1 : tier 0 partout (sauf en mode random : tier aléatoire)
// - Mode random (admin) : ignore le score, tier aléatoire chaque round
public static class SuperheroesModule
{
    private static bool _enabled;
    private static bool _debug;  // true → applique aussi aux bots (test autonome via RCON)
    private static ShAssignMode _assignMode = ShAssignMode.Noob;

    // Exposés pour VoteModule (libellés contextuels + check du mode courant).
    public static bool IsEnabled => _enabled;
    public static ShAssignMode CurrentAssignMode => _assignMode;

    // Helper unique pour décider si un joueur est éligible aux pouvoirs.
    // En debug, les bots sont inclus pour permettre de tester sans humain.
    private static bool IsEligible(CCSPlayerController p)
        => p.IsValid && (_debug || !p.IsBot);

    private sealed class RoundStats
    {
        public int Damage;
        public int Kills;
        public int Deaths;
        public int Plants;
        public int Defuses;
        public int Score => Damage + 50 * Kills + 100 * Plants + 100 * Defuses - 25 * Deaths;
    }

    // Stats du round en cours (rolling)
    private static readonly Dictionary<ulong, RoundStats> _stats = new();
    // Tier assigné pour le PROCHAIN spawn de ce joueur
    private static readonly Dictionary<ulong, int> _assignedTier = new();
    // Héros effectivement actif (mémo, pour affichage)
    private static readonly Dictionary<ulong, Hero> _activeHero = new();
    // Crédit de grenades "extra" restant à donner au joueur ce round. On give 1 au spawn
    // puis on re-give à chaque GrenadeThrown jusqu'à épuisement du crédit. Évite le
    // bug "2 nades du même type au spawn → la 2e tombe au sol" (inventory CS2 refuse
    // d'avoir 2× la même nade dans le même tick).
    private static readonly Dictionary<ulong, int> _extraNadeCredit = new();
    // Slot → server time auquel le freeze Hypnose expire. Re-appliqué chaque tick par
    // FreezeTick parce que CS2 réécrit MoveType à chaque tick après GrenadeThrown.
    private static readonly Dictionary<int, float> _hypnoseFrozenUntil = new();
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _freezeTimer;
    // True dès qu'on a fait au moins une fin de round avec stats → on a un historique
    private static bool _hasHistory;

    private static readonly Random _rng = new();
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _radarTimer;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _regenTimer;

    public static void Register()
    {
        // !sh                       → info héros actuel (tous joueurs)
        // !sh on/off                → activation (admin)
        // !sh noob|pgm|rdm          → mode d'attribution (admin)
        CS2UltimodPlugin.Commands.Register(new ChatCommand(
            "sh", ["hero", "superhero"], null, "",
            OnSh));

        CS2UltimodPlugin.EventBus.Subscribe<PlayerSpawnEvent>(OnSpawn);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerDeathEvent>(OnDeath);
        CS2UltimodPlugin.EventBus.Subscribe<PlayerHurtEvent>(OnHurt);
        CS2UltimodPlugin.EventBus.Subscribe<BombPlantedEvent>(e =>
        {
            if (!_enabled || e.Planter is not { IsValid: true } p || !IsEligible(p)) return;
            Get(p.SteamID).Plants++;
        });
        CS2UltimodPlugin.EventBus.Subscribe<BombDefusedEvent>(e =>
        {
            if (!_enabled || e.Defuser is not { IsValid: true } p || !IsEligible(p)) return;
            Get(p.SteamID).Defuses++;
        });
        CS2UltimodPlugin.EventBus.Subscribe<RoundEndEvent>(_ => OnRoundEnd());
        CS2UltimodPlugin.EventBus.Subscribe<RoundStartEvent>(_ =>
        {
            _stats.Clear();
            _extraNadeCredit.Clear();
            _hypnoseFrozenUntil.Clear();
        });

        // Re-give une grenade quand le buffé lance la sienne (au lieu de tout donner
        // au spawn et de voir la 2e tomber au sol).
        CS2UltimodPlugin.Instance.RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);

        // Tick periodique pour radar (0.4s ~ rythme natif du radar CS2)
        _radarTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(0.4f, RadarTick, TimerFlags.REPEAT);
        // Regen tick (1s)
        _regenTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(1.0f, RegenTick, TimerFlags.REPEAT);
        // Fast tick (0.05s = ~3 server ticks) : maintient les effets que CS2 reset
        // silencieusement à chaque tick (MOVETYPE_NONE pour Hypnose, VelocityModifier
        // pour les héros speed qui sinon perdent leur buff en jump).
        _freezeTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(0.05f, FastTick, TimerFlags.REPEAT);

        // Xray (voir à travers les murs) : système event-driven séparé,
        // basé sur prop_dynamic clones + CheckTransmit per-viewer.
        XrayGlow.Init();
    }

    private static bool IsModeActive() => CS2UltimodPlugin.ModeManager.IsActive(
        GameMode.Retake, GameMode.Execute, GameMode.Mixte, GameMode.Pickup);

    private static RoundStats Get(ulong sid)
    {
        if (!_stats.TryGetValue(sid, out var s)) { s = new RoundStats(); _stats[sid] = s; }
        return s;
    }

    private static void ClearAll()
    {
        _stats.Clear();
        _assignedTier.Clear();
        _activeHero.Clear();
        _extraNadeCredit.Clear();
        _hypnoseFrozenUntil.Clear();
        _hasHistory = false;
    }

    // Reset les effets persistants sur un pion. Appelé au début de ApplyHero (avant le nouveau
    // pouvoir) ET sur tous les pions vivants quand on désactive SH (sinon les effets traînent).
    private static void ResetPawnBaseline(CCSPlayerPawn pawn)
    {
        try
        {
            pawn.VelocityModifier = 1.0f;
            pawn.MaxHealth = 100;
            // Reset scale via Source 2 entity input (per-entity, contrairement à
            // SkeletonInstance.Scale qui semble être partagé entre les pawns
            // partageant le même model resource côté CS2).
            pawn.AcceptInput("SetScale", null, null, "1.0");
            // Reset alpha (Invisible précédent → opaque). m_clrRender ne reset pas seul.
            pawn.Render = System.Drawing.Color.FromArgb(255, 255, 255, 255);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        }
        catch (Exception ex)
        {
            CS2UltimodPlugin.Log?.LogWarning(ex, "[SH] ResetPawnBaseline failed");
        }
    }

    private static void ResetAllAlivePawns()
    {
        foreach (var p in PlayerExt.AllConnected())
        {
            if (!p.PawnIsAlive) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;
            ResetPawnBaseline(pawn);
        }
    }

    // ── Commandes ─────────────────────────────────────────────────────────────

    private static void OnSh(CCSPlayerController p, string[] args)
    {
        if (args.Length == 0) { ShowInfo(p); return; }

        var sub = args[0].ToLowerInvariant();
        bool isAdminSub = sub is "on" or "off" or "noob" or "pgm" or "rdm" or "debug" or "test";
        if (isAdminSub && !CS2UltimodPlugin.Permissions.RequireFlag(p, "@cs2ultimod/superheroes")) return;

        // !sh test <heroId> — applique un héros précis à l'appelant, sans passer
        // par le système d'attribution. Permet de tester un effet sans jouer un round.
        if (sub == "test")
        {
            if (args.Length < 2) { Chat.TellError(p, "Usage: !sh test <heroId>"); return; }
            ApplyTestHero(p, args[1]);
            return;
        }

        if (!ApplyAction(sub, p.PlayerName))
            ShowInfo(p);
    }

    // Mirror RCON-callable de OnSh, sans permission check (RCON = god mode).
    public static void HandleRconAction(string sub)
        => ApplyAction(sub.ToLowerInvariant(), "rcon");

    // Appliqué depuis VoteModule quand un vote !votesh aboutit.
    // Transition d'état unique (switch mode + ensure enabled) SANS broadcast :
    // le vote callback émet son propre message pour éviter les doublons type
    // "Superheroes activés" + "mode rattrapage" qui disent la même chose.
    public static void ApplyVoteResult(string sub)
    {
        switch (sub.ToLowerInvariant())
        {
            case "off":
                if (_enabled) { _enabled = false; ClearAll(); ResetAllAlivePawns(); }
                break;
            case "noob":
                _assignMode = ShAssignMode.Noob;
                if (!_enabled) { _enabled = true; _hasHistory = false; }
                break;
            case "pgm":
                _assignMode = ShAssignMode.Pgm;
                if (!_enabled) { _enabled = true; _hasHistory = false; }
                break;
            case "rdm":
                _assignMode = ShAssignMode.Rdm;
                if (!_enabled) { _enabled = true; _hasHistory = false; }
                break;
            default:
                return;
        }
        CS2UltimodPlugin.Log?.LogInformation("[SH] Vote applied: {Sub} → enabled={Enabled} mode={Mode}", sub, _enabled, _assignMode);
    }

    public static void HandleRconTest(string heroId, string playerNamePartial)
    {
        var p = PlayerExt.FindByName(playerNamePartial);
        if (p == null)
        {
            CS2UltimodPlugin.Log?.LogWarning("[SH] Test: player '{Name}' not found", playerNamePartial);
            return;
        }
        Server.NextFrame(() => ApplyTestHero(p, heroId));
    }

    // Cycle index pour `!sh test next` — cycle linéaire dans HeroCatalog.All.
    private static int _testCycleIndex;

    private static void ApplyTestHero(CCSPlayerController p, string heroId)
    {
        if (!p.IsValid) return;
        var key = heroId.ToLowerInvariant();

        if (key == "reset") { _testCycleIndex = 0; Chat.Tell(p, "[TEST] Cycle remis à 0."); return; }

        Hero? hero;
        int displayIdx = -1, total = HeroCatalog.All.Length;
        if (key == "next")
        {
            displayIdx = _testCycleIndex;
            hero = HeroCatalog.All[_testCycleIndex];
            _testCycleIndex = (_testCycleIndex + 1) % total;
        }
        else
        {
            hero = HeroCatalog.All.FirstOrDefault(h => h.Id == key);
        }

        if (hero == null)
        {
            Chat.TellError(p, $"Héros inconnu : {heroId}. IDs : {string.Join(", ", HeroCatalog.All.Select(h => h.Id))}");
            return;
        }
        if (!p.PawnIsAlive)
        {
            Chat.TellError(p, "Tu dois être vivant pour tester un héros.");
            return;
        }
        ApplyHero(p, hero);
        _activeHero[p.SteamID] = hero;
        CS2UltimodPlugin.Log?.LogInformation("[SH] TEST applied {Hero} (tier {Tier}) → {Player}",
            hero.Name, hero.Tier, p.PlayerName);
        var label = displayIdx >= 0 ? $"[TEST {displayIdx + 1}/{total}]" : "[TEST]";
        Chat.TellSuccess(p, $"{label} {hero.Name} (tier {(hero.Tier >= 0 ? "+" : "")}{hero.Tier}) — {hero.Description}");
    }

    private static bool ApplyAction(string sub, string actor)
    {
        switch (sub)
        {
            case "on":
                _enabled = true; _hasHistory = false;
                CS2UltimodPlugin.Log?.LogInformation("[SH] Enabled by {Actor} (mode {Mode})", actor, _assignMode);
                Chat.Broadcast($"Superheroes activés (mode {_assignMode}).");
                return true;
            case "off":
                _enabled = false; ClearAll();
                ResetAllAlivePawns();
                CS2UltimodPlugin.Log?.LogInformation("[SH] Disabled by {Actor}", actor);
                Chat.Broadcast("Superheroes désactivés.");
                return true;
            case "noob":
                _assignMode = ShAssignMode.Noob;
                CS2UltimodPlugin.Log?.LogInformation("[SH] Mode noob by {Actor}", actor);
                Chat.Broadcast($"Superheroes : mode rattrapage (par {actor}).");
                return true;
            case "pgm":
                _assignMode = ShAssignMode.Pgm;
                CS2UltimodPlugin.Log?.LogInformation("[SH] Mode pgm by {Actor}", actor);
                Chat.Broadcast($"Superheroes : mode récompense (par {actor}).");
                return true;
            case "rdm":
                _assignMode = ShAssignMode.Rdm;
                CS2UltimodPlugin.Log?.LogInformation("[SH] Mode rdm by {Actor}", actor);
                Chat.Broadcast($"Superheroes : mode aléatoire (par {actor}).");
                return true;
            case "debug":
                _debug = !_debug;
                CS2UltimodPlugin.Log?.LogInformation("[SH] Debug mode = {Debug} by {Actor}", _debug, actor);
                Chat.Broadcast($"Superheroes : debug = {_debug} (par {actor}).");
                return true;
            default:
                return false;
        }
    }

    private static void ShowInfo(CCSPlayerController p)
    {
        if (!_enabled) { Chat.TellError(p, "Superheroes désactivés."); return; }
        if (!_activeHero.TryGetValue(p.SteamID, out var h))
        {
            Chat.Tell(p, "Aucun héros actif. Apparaîtra au prochain spawn.");
            return;
        }
        Chat.Tell(p, $"Héros actuel : {h.Name} (tier {(h.Tier >= 0 ? "+" : "")}{h.Tier}) — {h.Description}");
    }

    // ── Tracking stats round courant ──────────────────────────────────────────

    private static void OnHurt(PlayerHurtEvent e)
    {
        if (!_enabled || !IsModeActive()) return;
        if (e.Victim is not { IsValid: true } vic) return;

        // Effet "Fragile" : extra dégâts post-coup. On n'écrit Health que si le résultat
        // reste strictement > 0 — sinon on ressusciterait un joueur que l'engine est en
        // train de tuer (le hit initial a déjà mis Health <= 0, on laisse la mort se finir).
        // Math.Max(1, ...) qu'on avait avant rendait Fragile immortel sur les coups létaux.
        // Pas de CommitSuicide pour les coups que l'extra rend létaux — risque de
        // re-entrance dans PlayerHurt → stack overflow → crash serveur.
        if (IsEligible(vic) && _activeHero.TryGetValue(vic.SteamID, out var vHero) && vHero.Effect == HeroEffect.DamageTaken)
        {
            try
            {
                var pawn = vic.PlayerPawn.Value;
                if (pawn != null && pawn.IsValid && vic.PawnIsAlive)
                {
                    int extra = (int)Math.Round(e.DamageHealth * (vHero.Param / 100f - 1f));
                    if (extra > 0)
                    {
                        int newHp = pawn.Health - extra;
                        if (newHp >= 1)
                        {
                            pawn.Health = newHp;
                            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                        }
                        // Si newHp <= 0 : on ne touche rien. Le coup initial a soit déjà tué
                        // (Health<=0 côté engine), soit laissé le joueur à un HP très bas que
                        // la prochaine balle achèvera.
                    }
                }
            }
            catch (Exception ex)
            {
                CS2UltimodPlugin.Log?.LogWarning(ex, "[SH] Fragile damage failure");
            }
        }

        if (e.Attacker is not { IsValid: true } atk || !IsEligible(atk)) return;
        if (atk.SteamID == vic.SteamID) return;
        if (atk.TeamNum == vic.TeamNum) return;
        Get(atk.SteamID).Damage += e.DamageHealth;
    }

    private static void OnDeath(PlayerDeathEvent e)
    {
        if (!_enabled || !IsModeActive()) return;
        if (e.Victim is { IsValid: true } v && IsEligible(v)) Get(v.SteamID).Deaths++;
        if (e.Attacker is { IsValid: true } a && IsEligible(a) && a.SteamID != e.Victim.SteamID && a.TeamNum != e.Victim.TeamNum)
            Get(a.SteamID).Kills++;
    }

    // EventGrenadeThrown.Weapon : nom court sans préfixe "weapon_" ("hegrenade",
    // "smokegrenade", "flashbang", "molotov", "incgrenade").
    private static HookResult OnGrenadeThrown(EventGrenadeThrown evt, GameEventInfo info)
    {
        var p = evt.Userid;
        if (p is not { IsValid: true } || !_activeHero.TryGetValue(p.SteamID, out var hero)) return HookResult.Continue;

        // Hypnose : décoy → freeze tous les ennemis vivants pendant hero.Param secondes.
        if (hero.Effect == HeroEffect.Hypnose && evt.Weapon == "decoy")
        {
            FreezeEnemies(p, hero.Param);
            return HookResult.Continue;
        }

        if (!_extraNadeCredit.TryGetValue(p.SteamID, out var credit) || credit <= 0) return HookResult.Continue;

        CsItem? give = (hero.Effect, evt.Weapon) switch
        {
            (HeroEffect.ExtraFlash, "flashbang") => CsItem.Flashbang,
            (HeroEffect.ExtraHE, "hegrenade") => CsItem.HE,
            (HeroEffect.ExtraSmoke, "smokegrenade") => CsItem.Smoke,
            (HeroEffect.ExtraMolotov, "molotov") => CsItem.Molotov,
            (HeroEffect.ExtraMolotov, "incgrenade") => CsItem.Incendiary,
            _ => null,
        };
        if (give == null) return HookResult.Continue;

        _extraNadeCredit[p.SteamID] = credit - 1;
        // NextFrame : l'event fire pendant que la nade quitte encore l'inventaire ;
        // donner dans le même tick reproduirait le bug initial (slot pas libre).
        var item = give.Value;
        Server.NextFrame(() =>
        {
            if (p.IsValid && p.PawnIsAlive) p.GiveNamedItem(item);
        });
        return HookResult.Continue;
    }

    // Gèle tous les ennemis vivants de `thrower` pendant `seconds` secondes.
    // Implémentation : on marque "frozen until now+sec" par slot ; FreezeTick re-applique
    // MOVETYPE_NONE chaque 0.05s parce que CS2 réécrit le MoveType après GrenadeThrown
    // (sinon le freeze ne tient qu'~1 tick).
    private static void FreezeEnemies(CCSPlayerController thrower, int seconds)
    {
        if (seconds <= 0) return;
        byte throwerTeam = thrower.TeamNum;
        float until = Server.CurrentTime + seconds;
        int count = 0;

        foreach (var enemy in Utilities.GetPlayers())
        {
            if (!enemy.IsValid || !enemy.PawnIsAlive) continue;
            if (enemy.TeamNum == throwerTeam) continue;
            if (enemy.TeamNum != (byte)CsTeam.Terrorist && enemy.TeamNum != (byte)CsTeam.CounterTerrorist) continue;
            _hypnoseFrozenUntil[enemy.Slot] = until;
            count++;
            // Premier set immédiat (FreezeTick re-écrira ensuite si CS2 reset).
            var pawn = enemy.PlayerPawn.Value;
            if (pawn == null) continue;
            pawn.MoveType = MoveType_t.MOVETYPE_NONE;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        }

        if (count == 0) return;
        Chat.Broadcast($"[SH] {thrower.PlayerName} hypnotise l'équipe adverse ({seconds}s).");
        CS2UltimodPlugin.Log?.LogInformation(
            "[SH] Hypnose by {Thrower}: {Count} enemy(ies) marked frozen until t={Until} (now={Now})",
            thrower.PlayerName, count, until, Server.CurrentTime);
    }

    // Combine freeze (Hypnose) + speed enforcement (SpeedMul/Spy). Mêmes contraintes
    // tick CS2 : on doit réécrire à haute fréquence sinon l'engine reset.
    private static void FastTick()
    {
        SpeedEnforceTick();
        FreezeTick();
    }

    private static void SpeedEnforceTick()
    {
        if (!IsModeActive()) return;
        foreach (var (sid, hero) in _activeHero)
        {
            float? mul = hero.Effect switch
            {
                HeroEffect.SpeedMul => 1.0f + hero.Param / 100f,
                HeroEffect.Spy => 2.0f,
                _ => null,
            };
            if (mul == null) continue;
            var p = PlayerExt.FindBySteamId(sid);
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;
            // VelocityModifier décline et est reset par l'engine sur certains events
            // (notamment jump). Réécriture à 0.05s = buff perçu même en l'air.
            pawn.VelocityModifier = mul.Value;
        }
    }

    private static void FreezeTick()
    {
        if (_hypnoseFrozenUntil.Count == 0) return;
        var now = Server.CurrentTime;
        List<int>? expired = null;

        foreach (var (slot, until) in _hypnoseFrozenUntil)
        {
            if (now >= until) { (expired ??= new()).Add(slot); continue; }
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is not { IsValid: true, PawnIsAlive: true }) { (expired ??= new()).Add(slot); continue; }
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;

            // Force MoveType chaque tick (CS2 le reset silencieusement sinon).
            pawn.MoveType = MoveType_t.MOVETYPE_NONE;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
            // Zero velocity : MoveType=NONE seul ne stoppe pas le mouvement déjà engagé.
            // L'engine continue d'intégrer la vélocité du tick précédent.
            var v = pawn.AbsVelocity;
            v.X = 0; v.Y = 0; v.Z = 0;

            // Diag : sample 1x par ~0.5s pour voir l'état réel.
            if (((int)(now * 10)) % 5 == 0)
            {
                CS2UltimodPlugin.Log?.LogInformation(
                    "[SH] FreezeTick slot={Slot} mt={Mt} vel=({Vx},{Vy},{Vz}) until={Until} now={Now}",
                    slot, pawn.MoveType, v.X, v.Y, v.Z, until, now);
            }
        }

        if (expired == null) return;
        foreach (var slot in expired)
        {
            _hypnoseFrozenUntil.Remove(slot);
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;
            // Ne dégèle que si toujours frozen (sinon un autre système a déjà changé).
            if (pawn.MoveType != MoveType_t.MOVETYPE_NONE) continue;
            pawn.MoveType = MoveType_t.MOVETYPE_WALK;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
            CS2UltimodPlugin.Log?.LogInformation("[SH] Hypnose unfreeze slot={Slot} t={Now}", slot, now);
        }
    }

    // ── Fin de round → ranking → tier prochain round ──────────────────────────

    private static void OnRoundEnd()
    {
        if (!_enabled || !IsModeActive()) return;
        try { DoRoundEnd(); }
        catch (Exception ex) { CS2UltimodPlugin.Log?.LogError(ex, "[SH] OnRoundEnd crashed"); }
    }

    private static void DoRoundEnd()
    {

        var prevAssignments = new Dictionary<ulong, int>(_assignedTier);
        _assignedTier.Clear();

        if (_assignMode == ShAssignMode.Rdm)
        {
            // En Rdm, le tier importe peu : OnSpawn tire un héros random sur tout le catalogue.
            // On laisse _assignedTier vide.
        }
        else if (CS2UltimodPlugin.ModeManager.IsActive(GameMode.Pickup))
        {
            AssignByTeamRanking();
        }
        else
        {
            AssignByGlobalRanking();
        }

        _hasHistory = true;

        // MVP annonce
        if (_stats.Count > 0)
        {
            var top = _stats.MaxBy(kv => kv.Value.Score);
            if (top.Key != 0)
            {
                var topPlayer = PlayerExt.FindBySteamId(top.Key);
                var heroName = _activeHero.TryGetValue(top.Key, out var h) ? h.Name : "?";
                if (topPlayer != null)
                    Chat.Broadcast($"[SH] MVP : {topPlayer.PlayerName} ({heroName}) — score {top.Value.Score}");
            }
        }
    }

    private static void AssignByGlobalRanking()
    {
        var ranked = PlayerExt.AllConnected()
            .Where(IsEligible)
            .Select(p => (Sid: p.SteamID, Score: _stats.TryGetValue(p.SteamID, out var s) ? s.Score : 0))
            .OrderByDescending(x => x.Score)
            .ToList();

        int n = ranked.Count;
        if (n == 0) return;

        for (int i = 0; i < n; i++)
            _assignedTier[ranked[i].Sid] = TierForPosition(i, n);
    }

    // Mappe la position i parmi n joueurs vers un tier -2..+2 (sens "noob").
    // n=1 → 0 ; n=2/3 → top et bottom tirés au sort entre magnitude 1 et 2 pour
    // varier l'intensité (sinon n=3 renvoyait toujours -2,0,+2 — jamais ±1).
    // n≥4 → quintile uniforme via Math.Round((i*4)/(n-1)) :
    // - n=4 → 0,1,3,4 (skip neutre, pas grave)
    // - n=5 → 0,1,2,3,4 (distribution parfaite)
    private static int TierForPosition(int i, int n)
    {
        if (n <= 1) return 0;

        if (n <= 3)
        {
            // Position centrale (n=3 et i=1) reste neutre.
            if (n == 3 && i == 1) return 0;
            var sign = i == 0 ? -1 : 1;
            var mag = _rng.Next(2) + 1;  // 1 ou 2
            return ApplyMode(sign * mag);
        }

        var q = (int)Math.Round((double)i * 4 / (n - 1));
        var noob = q switch { 0 => -2, 1 => -1, 2 => 0, 3 => 1, _ => 2 };
        return ApplyMode(noob);
    }

    // Noob (rattrapage) : best → -2 (nerf), worst → +2 (buff)
    // Pgm  (récompense) : best → +2 (buff), worst → -2 (nerf)
    private static int ApplyMode(int noobTier)
        => _assignMode == ShAssignMode.Pgm ? -noobTier : noobTier;

    private static void AssignByTeamRanking()
    {
        foreach (var team in new[] { (byte)CsTeam.Terrorist, (byte)CsTeam.CounterTerrorist })
        {
            var ranked = PlayerExt.AllConnected()
                .Where(p => IsEligible(p) && p.TeamNum == team)
                .Select(p => (Sid: p.SteamID, Score: _stats.TryGetValue(p.SteamID, out var s) ? s.Score : 0))
                .OrderByDescending(x => x.Score)
                .ToList();

            int n = ranked.Count;
            if (n == 0) continue;

            for (int i = 0; i < n; i++)
                _assignedTier[ranked[i].Sid] = TierForPosition(i, n);
        }
    }

    // ── Spawn → applique le héros du tier assigné ─────────────────────────────

    private static void OnSpawn(PlayerSpawnEvent e)
    {
        if (!_enabled || !IsModeActive()) return;
        var p = e.Player;
        if (!IsEligible(p)) return;

        Hero hero;
        if (_assignMode == ShAssignMode.Rdm)
        {
            // Tirage uniforme sur tout le catalogue, indépendant pour chaque joueur, à chaque spawn.
            hero = HeroCatalog.All[_rng.Next(HeroCatalog.All.Length)];
        }
        else
        {
            int tier = _hasHistory && _assignedTier.TryGetValue(p.SteamID, out var t) ? t : 0;
            var pool = HeroCatalog.OfTier(tier);
            if (pool.Length == 0) return;
            hero = pool[_rng.Next(pool.Length)];
        }
        _activeHero[p.SteamID] = hero;

        // Délai de 2 frames : Server.NextFrame seul est parfois trop tôt, le pion n'a pas
        // ses sub-components (BodyComponent, Skeleton) entièrement initialisés au tick suivant.
        Server.NextFrame(() => Server.NextFrame(() => SafeApplyHero(p, hero)));
    }

    private static void SafeApplyHero(CCSPlayerController player, Hero hero)
    {
        try
        {
            ApplyHero(player, hero);
            CS2UltimodPlugin.Log?.LogInformation("[SH] Apply {Hero} (tier {Tier}) → {Player}",
                hero.Name, hero.Tier, player.PlayerName);
        }
        catch (Exception ex)
        {
            CS2UltimodPlugin.Log?.LogError(ex, "[SH] ApplyHero crashed for {Hero} on {Player}",
                hero.Name, player.PlayerName);
        }
    }

    private static void ApplyHero(CCSPlayerController player, Hero hero)
    {
        if (!player.IsValid || !player.PawnIsAlive) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        // Baseline reset : effacer les effets persistants du héros précédent.
        // ModelScale et MaxHealth ne sont pas reset par CS2 entre les rounds, ils traînent
        // sinon (Géant 1.4× → Quidam → reste 1.4×).
        ResetPawnBaseline(pawn);

        switch (hero.Effect)
        {
            case HeroEffect.BonusHp:
                var newHp = Math.Max(1, pawn.Health + hero.Param);
                pawn.Health = newHp;
                if (newHp > pawn.MaxHealth) pawn.MaxHealth = newHp;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                break;

            case HeroEffect.BonusArmor:
                pawn.ArmorValue = Math.Clamp(pawn.ArmorValue + hero.Param, 0, 200);
                break;

            case HeroEffect.SpeedMul:
                pawn.VelocityModifier = 1.0f + hero.Param / 100f;
                break;

            case HeroEffect.ModelScale:
                // Source 2 entity input "SetScale" : per-entity, contrairement à
                // SkeletonInstance.Scale qui propage le scale à tous les pions
                // partageant le même model resource (= tout le monde en pratique).
                var scale = hero.Param / 100f;
                pawn.AcceptInput("SetScale", null, null, scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;

            case HeroEffect.ExtraFlash:
                player.GiveNamedItem(CsItem.Flashbang);
                _extraNadeCredit[player.SteamID] = Math.Max(0, hero.Param - 1);
                break;
            case HeroEffect.ExtraHE:
                player.GiveNamedItem(CsItem.HE);
                _extraNadeCredit[player.SteamID] = Math.Max(0, hero.Param - 1);
                break;
            case HeroEffect.ExtraSmoke:
                player.GiveNamedItem(CsItem.Smoke);
                _extraNadeCredit[player.SteamID] = Math.Max(0, hero.Param - 1);
                break;
            case HeroEffect.ExtraMolotov:
                var molly = player.TeamNum == (byte)CsTeam.Terrorist ? CsItem.Molotov : CsItem.Incendiary;
                player.GiveNamedItem(molly);
                _extraNadeCredit[player.SteamID] = Math.Max(0, hero.Param - 1);
                break;

            case HeroEffect.NoNades:
                Server.NextFrame(() => StripNades(player));
                break;

            case HeroEffect.Hypnose:
                player.GiveNamedItem(CsItem.Decoy);
                break;

            case HeroEffect.Invisible:
                // Alpha 0 sur le modèle. Hitbox/collision intactes.
                // L'arme tenue est une entity séparée → reste visible (volontaire).
                pawn.Render = System.Drawing.Color.FromArgb(0, 255, 255, 255);
                Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
                break;

            case HeroEffect.Spy:
                pawn.VelocityModifier = 2.0f; // speed ×2 (rewrite chaque 0.05s par SpeedTick)
                pawn.AcceptInput("SetScale", null, null, "0.5");
                Server.NextFrame(() => StripNonKnife(player));
                break;
        }

        Chat.Tell(player, $"[SH] {hero.Name} — {hero.Description}");
    }

    private static void StripNades(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid || !player.PawnIsAlive) return;
            var pawn = player.PlayerPawn.Value;
            if (pawn?.WeaponServices == null) return;

            // Snapshot puis retrait : iterating + modifying MyWeapons est unsafe.
            var toRemove = new List<CBasePlayerWeapon>();
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var w = handle.Value;
                if (w == null || !w.IsValid) continue;
                if (IsNade(w.DesignerName)) toRemove.Add(w);
            }

            foreach (var w in toRemove)
            {
                try
                {
                    // AcceptInput("Kill") = méthode Source 2 sûre pour deleter une entité,
                    // décrémente proprement les refs du game manager.
                    // weapon.Remove() direct était la cause du crash serveur précédent
                    // (dangling pointer côté CS2 native code).
                    w.AcceptInput("Kill");
                }
                catch (Exception ex)
                {
                    CS2UltimodPlugin.Log?.LogWarning(ex, "[SH] StripNades kill failed for {Designer}", w.DesignerName);
                }
            }
            CS2UltimodPlugin.Log?.LogInformation("[SH] StripNades removed {Count} nade(s) from {Player}",
                toRemove.Count, player.PlayerName);
        }
        catch (Exception ex)
        {
            CS2UltimodPlugin.Log?.LogError(ex, "[SH] StripNades crashed for {Player}", player.PlayerName);
        }
    }

    private static bool IsNade(string designer) =>
        designer.Contains("flashbang") || designer.Contains("hegrenade") ||
        designer.Contains("smoke") || designer.Contains("molotov") ||
        designer.Contains("incgrenade") || designer.Contains("decoy");

    // Garde tout designer qui commence par "weapon_knife" (toutes variantes T/CT/skins).
    private static bool IsKnife(string designer) => designer.StartsWith("weapon_knife");

    // Retire toute arme non-couteau de l'inventaire. Utilisé par Spy au spawn
    // et à chaque RadarTick pour empêcher le pickup d'armes au sol.
    // Implé via player.RemoveWeapons() + redonner un couteau : path safe utilisé
    // par l'allocator. L'approche manuelle (AcceptInput("Kill") sur chaque weapon
    // entity) crash le serveur côté CS2 (dangling pointer, cf. Manchot 2026-05-07).
    private static void StripNonKnife(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid || !player.PawnIsAlive) return;
            var pawn = player.PlayerPawn.Value;
            if (pawn?.WeaponServices == null) return;

            // Skip si déjà uniquement couteau (cas du re-tick après strip initial).
            bool hasNonKnife = false;
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var w = handle.Value;
                if (w == null || !w.IsValid) continue;
                if (!IsKnife(w.DesignerName)) { hasNonKnife = true; break; }
            }
            if (!hasNonKnife) return;

            player.RemoveWeapons();
            player.GiveNamedItem(CsItem.Knife);
        }
        catch (Exception ex)
        {
            CS2UltimodPlugin.Log?.LogError(ex, "[SH] StripNonKnife crashed for {Player}", player.PlayerName);
        }
    }

    // ── Tick handlers : radar + regen ─────────────────────────────────────────

    private static void RadarTick()
    {
        // Pas de check _enabled ici : les effets s'appliquent dès que _activeHero a une entrée
        // (assignment normal OU !sh test). _activeHero est vidé à `!sh off` via ClearAll.
        if (!IsModeActive()) return;

        // Speed (SpeedMul/Spy) : géré par FastTick (0.05s) — ici on garde uniquement
        // les choses qui peuvent tourner à 0.4s sans dégrader le ressenti.

        // Spy : re-strip toute arme non-couteau pour empêcher le pickup au sol.
        foreach (var (sid, hero) in _activeHero)
        {
            if (hero.Effect != HeroEffect.Spy) continue;
            var p = PlayerExt.FindBySteamId(sid);
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            StripNonKnife(p);
        }

        // Tier-list des équipes ennemies à spotter (selon les radar-buffés vivants).
        // Xray est géré séparément par XrayGlow (event-driven + CheckTransmit listener).
        bool tNeedsSpotting = false; // au moins un CT a Radar → on spot les T sur radar
        bool ctNeedsSpotting = false;

        foreach (var (sid, hero) in _activeHero)
        {
            if (hero.Effect != HeroEffect.Radar) continue;
            var p = PlayerExt.FindBySteamId(sid);
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            if (p.TeamNum == (byte)CsTeam.CounterTerrorist) tNeedsSpotting = true;
            else if (p.TeamNum == (byte)CsTeam.Terrorist) ctNeedsSpotting = true;
        }

        // Cvar `ammo_grenade_limit_default 2` permet à TOUT le monde de porter 2× chaque
        // grenade. On enforce manuellement la limite vanilla (1× HE/smoke/molly) sur les
        // non-buffés ; les buffés (Boum/Fumeur/Pyromane) gardent leurs 2. Flash exempt
        // car vanilla autorise déjà 2.
        EnforceGrenadeLimits();

if (!tNeedsSpotting && !ctNeedsSpotting) return;

        foreach (var enemy in Utilities.GetPlayers())
        {
            if (!enemy.IsValid || !enemy.PawnIsAlive) continue;
            bool isT = enemy.TeamNum == (byte)CsTeam.Terrorist;
            bool isCT = enemy.TeamNum == (byte)CsTeam.CounterTerrorist;
            if (!((isT && tNeedsSpotting) || (isCT && ctNeedsSpotting))) continue;

            var pawn = enemy.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;
            var spotted = pawn.EntitySpottedState;
            if (spotted == null) continue;

            try
            {
                spotted.SpottedByMask[0] = uint.MaxValue;
                spotted.SpottedByMask[1] = uint.MaxValue;
                // Schema path correct pour CS2 : CCSPlayerPawnBase::m_entitySpottedState
                // (et non CBaseEntity::m_bSpottedByMask qui ne propage pas).
                Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_entitySpottedState");
            }
            catch (Exception ex)
            {
                CS2UltimodPlugin.Log?.LogWarning(ex, "[SH] RadarTick spot failure");
            }
        }
    }


    // Designer names des grenades sous quota strict (1× max sauf buff).
    // Flash absent : vanilla CS2 autorise déjà 2 flashes par joueur.
    private static readonly string[] _restrictedNades =
        ["weapon_hegrenade", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade"];

    // Renvoie le designer name que ce joueur a le droit de porter en 2 exemplaires
    // grâce à son héros (null sinon).
    private static string? GetBuffedNadeDesigner(CCSPlayerController p)
    {
        if (!_activeHero.TryGetValue(p.SteamID, out var hero)) return null;
        return hero.Effect switch
        {
            HeroEffect.ExtraHE => "weapon_hegrenade",
            HeroEffect.ExtraSmoke => "weapon_smokegrenade",
            HeroEffect.ExtraMolotov => p.TeamNum == (byte)CsTeam.Terrorist
                ? "weapon_molotov" : "weapon_incgrenade",
            _ => null,
        };
    }

    private static void EnforceGrenadeLimits()
    {
        foreach (var p in PlayerExt.AllConnected())
        {
            if (!p.IsValid || !p.PawnIsAlive) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn?.WeaponServices == null) continue;

            var allowedExtra = GetBuffedNadeDesigner(p);

            // Snapshot des grenades portées, regroupées par designer name.
            var groups = new Dictionary<string, List<CBasePlayerWeapon>>();
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var w = handle.Value;
                if (w == null || !w.IsValid) continue;
                var dn = w.DesignerName;
                if (!_restrictedNades.Contains(dn)) continue;
                if (!groups.TryGetValue(dn, out var list)) { list = new(); groups[dn] = list; }
                list.Add(w);
            }

            foreach (var (dn, list) in groups)
            {
                int max = (dn == allowedExtra) ? 2 : 1;
                if (list.Count <= max) continue;
                // Kill via AcceptInput (même pattern safe que StripNades pour Manchot).
                for (int i = max; i < list.Count; i++)
                {
                    try { list[i].AcceptInput("Kill"); }
                    catch (Exception ex)
                    {
                        CS2UltimodPlugin.Log?.LogWarning(ex,
                            "[SH] EnforceGrenadeLimits Kill failed for {Designer} on {Player}",
                            dn, p.PlayerName);
                    }
                }
            }
        }
    }

    private static void RegenTick()
    {
        // Idem RadarTick : pas de check _enabled, _activeHero est la source de vérité.
        if (!IsModeActive()) return;

        foreach (var (sid, hero) in _activeHero)
        {
            if (hero.Effect != HeroEffect.Regen) continue;
            var p = PlayerExt.FindBySteamId(sid);
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;
            if (pawn.Health >= pawn.MaxHealth) continue;
            pawn.Health = Math.Min(pawn.MaxHealth, pawn.Health + hero.Param);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
    }

    // ── Xray (wallhack à travers les murs) ────────────────────────────────────
    // Pattern canonique CS2 (Glow direct sur pawn vivant ne marche pas) : on crée
    // 2 prop_dynamic par cible — un relay invisible qui suit le pawn, un glow qui
    // suit le relay. Animations propagées via la chaîne FollowEntity. Visibilité
    // filtrée per-viewer via Listeners.CheckTransmit selon le buff Xray du viewer.
    // Référence : github.com/labaland/plugin-wallhack
    private static class XrayGlow
    {
        private sealed class GlowData
        {
            public CDynamicProp? Relay;
            public CDynamicProp? Glow;
        }

        private static readonly Dictionary<int, GlowData> _data = new();
        private static readonly HashSet<int> _pendingSlots = new();
        private static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            var plugin = CS2UltimodPlugin.Instance;
            plugin.RegisterEventHandler<EventPlayerSpawn>((evt, info) =>
            {
                var p = evt.Userid;
                if (p != null && p.IsValid) ScheduleGlow(p);
                return HookResult.Continue;
            }, HookMode.Post);

            plugin.RegisterEventHandler<EventPlayerDeath>((evt, info) =>
            {
                var p = evt.Userid;
                if (p != null) RemoveGlow(p.Slot);
                return HookResult.Continue;
            });

            plugin.RegisterEventHandler<EventPlayerDisconnect>((evt, info) =>
            {
                var p = evt.Userid;
                if (p != null) { _pendingSlots.Remove(p.Slot); RemoveGlow(p.Slot); }
                return HookResult.Continue;
            });

            plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

            // Scan initial pour les joueurs déjà vivants au plugin load (ex: hot reload).
            new CounterStrikeSharp.API.Modules.Timers.Timer(0.5f, () =>
            {
                foreach (var p in Utilities.GetPlayers())
                    if (p.IsValid && p.PawnIsAlive) ScheduleGlow(p);
            });
        }

        private static void ScheduleGlow(CCSPlayerController player)
        {
            var slot = player.Slot;
            _pendingSlots.Remove(slot);
            RemoveGlow(slot);
            if (!_pendingSlots.Add(slot)) return;

            // 0.20s : laisse le model loader finir avant qu'on récupère le path .vmdl.
            new CounterStrikeSharp.API.Modules.Timers.Timer(0.20f, () =>
            {
                _pendingSlots.Remove(slot);
                if (!player.IsValid || !player.PawnIsAlive) return;
                RemoveGlow(slot);
                CreateGlow(player);
            });
        }

        private static void RemoveGlow(int slot)
        {
            if (!_data.TryGetValue(slot, out var data)) return;
            try { if (data.Glow != null && data.Glow.IsValid) data.Glow.Remove(); } catch { }
            try { if (data.Relay != null && data.Relay.IsValid) data.Relay.Remove(); } catch { }
            _data.Remove(slot);
        }

        private static void CreateGlow(CCSPlayerController player)
        {
            if (_data.ContainsKey(player.Slot)) return;
            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || !player.PawnIsAlive) return;

            string? model = null;
            try
            {
                var skel = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance();
                model = skel?.ModelState.ModelName;
            }
            catch (Exception ex)
            {
                CS2UltimodPlugin.Log?.LogWarning(ex, "[SH-Xray] model name fetch failed for {Player}", player.PlayerName);
                return;
            }
            if (string.IsNullOrWhiteSpace(model) || !model.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)) return;

            var relay = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            var glow = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            if (relay == null || glow == null) return;

            relay.Spawnflags = 256;
            relay.Render = System.Drawing.Color.Transparent;
            relay.RenderMode = (RenderMode_t)10;  // kRenderNone

            glow.Spawnflags = 256;
            // Alpha 1 obligatoire : le glow ne s'affiche que si l'entité elle-même a
            // un Render non-transparent. Couleur invisible mais alpha=1 = ok.
            glow.Render = System.Drawing.Color.FromArgb(1, 0, 0, 0);

            relay.SetModel(model);
            glow.SetModel(model);

            relay.DispatchSpawn();
            glow.DispatchSpawn();

            glow.Glow.GlowRange = 5000;
            glow.Glow.GlowRangeMin = 0;
            glow.Glow.GlowColorOverride = System.Drawing.Color.Red;
            // GlowTeam = équipe ENNEMIE de la cible (mécanisme interne CS2 pour
            // l'outline through-walls).
            glow.Glow.GlowTeam = player.Team == CsTeam.Terrorist
                ? (int)CsTeam.CounterTerrorist
                : (int)CsTeam.Terrorist;
            glow.Glow.GlowType = 3;

            _data[player.Slot] = new GlowData { Relay = relay, Glow = glow };

            // FollowEntity en NextFrame : les props doivent exister avant le binding.
            // Chaîne relay → live_pawn (anim source) ; glow → relay (visuel). Sans
            // le relay intermédiaire le glow apparaît en T-pose.
            Server.NextFrame(() =>
            {
                if (!player.IsValid || !player.PawnIsAlive) { RemoveGlow(player.Slot); return; }
                if (!relay.IsValid || !glow.IsValid) { RemoveGlow(player.Slot); return; }
                var live = player.PlayerPawn?.Value;
                if (live == null || !live.IsValid) { RemoveGlow(player.Slot); return; }

                relay.AcceptInput("FollowEntity", live, relay, "!activator");
                glow.AcceptInput("FollowEntity", relay, glow, "!activator");
            });
        }

        private static void OnCheckTransmit(CCheckTransmitInfoList infoList)
        {
            foreach (var (info, viewer) in infoList)
            {
                if (viewer == null || !viewer.IsValid) continue;

                bool viewerHasXray = _activeHero.TryGetValue(viewer.SteamID, out var h)
                                     && h.Effect == HeroEffect.Xray;

                foreach (var slot in _data.Keys.ToList())
                {
                    var data = _data[slot];
                    if (data.Relay == null || data.Glow == null
                        || !data.Relay.IsValid || !data.Glow.IsValid)
                    {
                        RemoveGlow(slot);
                        continue;
                    }

                    var target = Utilities.GetPlayerFromSlot(slot);
                    bool shouldSee = viewerHasXray
                                     && target != null
                                     && target.IsValid
                                     && target.PawnIsAlive
                                     && target.Slot != viewer.Slot
                                     && viewer.Team != CsTeam.Spectator
                                     && target.Team != CsTeam.Spectator
                                     && target.Team != viewer.Team;

                    if (shouldSee)
                    {
                        info.TransmitEntities.Add(data.Relay);
                        info.TransmitEntities.Add(data.Glow);
                    }
                    else
                    {
                        info.TransmitEntities.Remove(data.Relay);
                        info.TransmitEntities.Remove(data.Glow);
                    }
                }
            }
        }
    }
}
