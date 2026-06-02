# INTERFACES.md — Contrats Phase 0

> **Document de référence** pour tous les développeurs/agents bossant sur les tracks Phase 1.
> Ces signatures sont **stables**. Toute évolution doit être validée et propagée.

## Vue d'ensemble

```
┌──────────────────────────────────────────────────────────┐
│                  CS2UltimodPlugin (entry)                │
└────┬───────┬────────┬──────────┬──────────┬──────────────┘
     │       │        │          │          │
     ▼       ▼        ▼          ▼          ▼
  Database  Mode    Menu       Perm      ChatHelpers
  (F2)     Manager  Framework  System    + PlayerExt
           (F3)    (F4)       (F5)      (F6)
     │       │        │          │          │
     └───────┴────┬───┴──────────┴──────────┘
                  │
        Tracks Phase 1 consomment ces contrats
```

---

## 1. Database (`Core/Database/`)

### Connexion & migrations

```csharp
namespace CS2Ultimod.Core.Database;

public interface IDatabase
{
    // Exécute une requête (INSERT / UPDATE / DELETE / DDL).
    Task<int> ExecuteAsync(string sql, object? parameters = null);

    // Récupère un seul résultat (ou null).
    Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null);

    // Récupère une liste de résultats.
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null);

    // Transaction.
    Task<T> InTransactionAsync<T>(Func<Task<T>> work);
}
```

### Migrations versionnées

Chaque module qui ajoute des tables fournit une **migration** :

```csharp
public interface IMigration
{
    string Id { get; }          // unique, ex: "admin_v1_init"
    int Version { get; }        // ordre d'exécution global
    Task UpAsync(IDatabase db);
}
```

**Préfixes de tables réservés** (1 par module, **interdiction** d'écrire en dehors) :
- `core_*`             — foundation (admins, kv-store, audit)
- `retake_*`           — Track A
- `execute_*`          — Track B
- `stuff_*`            — Track C (probablement vide)
- `admin_*`            — Track D (bans, mutes, gags)
- `allocator_*`        — Track E (préférences armes)
- `superheroes_*`      — Track F
- `votes_*`            — Track G
- `pickup_*`           — Track H (matches, faceit_links, stats)

**Enregistrement :**
```csharp
DatabaseRegistry.Register(new MyModuleMigration());
```

Toutes les migrations sont exécutées au boot dans l'ordre `Version` ASC, idempotentes.

---

## 2. Mode Manager (`Core/ModeManager.cs`)

```csharp
namespace CS2Ultimod.Core;

public enum GameMode
{
    Retake,
    Execute,
    Mixte,
    Stuff,
    Pickup
}

public interface IModeManager
{
    GameMode Current { get; }
    GameMode Default { get; }       // = Retake (boot)

    event Action<GameMode, GameMode>? OnModeChanged;  // (previous, next)

    // Switch de mode. Recharge la map en config standard si demandé.
    Task SwitchToAsync(GameMode mode, bool reloadMap = true, string? reason = null);

    // Helper pour les modules qui veulent s'activer/se désactiver selon le mode.
    bool IsActive(params GameMode[] modes);  // ex: IsActive(GameMode.Retake, GameMode.Execute)
}
```

### Pattern de routing d'events

Les modules **ne hookent pas directement** les events CSS. Ils s'abonnent via le `ModeAwareEventBus` :

```csharp
public interface IModeAwareEventBus
{
    // Le handler n'est invoqué que si le mode courant fait partie de `modes`.
    void Subscribe<TEvent>(Action<TEvent> handler, params GameMode[] modes) where TEvent : GameEvent;
}

// Évents communs publiés par la foundation (jamais hookés directement par les tracks) :
public abstract record GameEvent;
public record RoundStartEvent(int RoundNumber) : GameEvent;
public record RoundEndEvent(CsTeam Winner, int Score_T, int Score_CT) : GameEvent;
public record PlayerSpawnEvent(CCSPlayerController Player) : GameEvent;
public record PlayerDeathEvent(CCSPlayerController Victim, CCSPlayerController? Attacker, string Weapon, bool Headshot) : GameEvent;
public record BombPlantedEvent(CCSPlayerController Planter) : GameEvent;
public record BombDefusedEvent(CCSPlayerController Defuser) : GameEvent;
public record MapStartEvent(string MapName) : GameEvent;
```

**Pourquoi :** garantit qu'un round_start de mode pickup ne déclenche pas accidentellement la logique retake.

---

## 3. Menu framework (`Core/Menu/`)

```csharp
namespace CS2Ultimod.Core.Menu;

public interface IMenu
{
    string Title { get; set; }
    IMenu AddItem(string label, Action<CCSPlayerController> onSelect, bool enabled = true);
    IMenu AddSubmenu(string label, IMenu submenu);
    IMenu AddBack();                                 // ajoute un retour si pas root
    void Open(CCSPlayerController player);
    void Close(CCSPlayerController player);
}

public interface IMenuFramework
{
    IMenu Create(string title);

    // Helpers communs
    IMenu CreateConfirm(string question, Action<CCSPlayerController> onYes, Action<CCSPlayerController>? onNo = null);
    IMenu CreatePlayerPicker(string title, Action<CCSPlayerController, CCSPlayerController> onPick, Func<CCSPlayerController, bool>? filter = null);
}
```

**Navigation :**
- `W/S` (forward/back) : déplace curseur
- `E` (use) : valide
- `R` (reload) : retour menu parent
- `0` (mute key) : ferme
- L'affichage utilise `CenterHtmlScreen` ou équivalent CSS — implémentation interne, agents ne touchent pas.

**Exemple d'usage par un track :**
```csharp
var menu = _menus.Create("Admin Panel");
menu.AddItem("Kick player", p => OpenKickMenu(p));
menu.AddItem("Ban player",  p => OpenBanMenu(p));
menu.AddItem("Change map",  p => OpenMapMenu(p), enabled: HasFlag(p, "@css/changemap"));
menu.Open(player);
```

---

## 4. Permission system (`Core/Permissions/`)

```csharp
namespace CS2Ultimod.Core.Permissions;

public interface IPermissionService
{
    // Renvoie true si le joueur a le flag (ou un flag qui le contient, ex: @css/root).
    bool HasFlag(CCSPlayerController player, string flag);

    // Refuse silencieusement avec message FR si le flag manque. Renvoie true si OK.
    bool RequireFlag(CCSPlayerController player, string flag);

    // Pour gestion admin via SQLite (Track D consomme ça).
    Task<IReadOnlyList<string>> GetFlagsAsync(ulong steamId64);
    Task SetFlagsAsync(ulong steamId64, IEnumerable<string> flags, DateTimeOffset? expiresAt = null);
    Task RemoveAsync(ulong steamId64);
    Task ReloadAsync();
}
```

**Flags standard (CSS-compat) :**
- `@css/root` — superadmin
- `@css/ban`, `@css/kick`, `@css/chat`, `@css/changemap`, `@css/cvar`, `@css/rcon`, `@css/slay`, `@css/generic`
- `@cs2ultimod/mode` — switch de mode
- `@cs2ultimod/edit` — éditeur spawns/scenarios
- `@cs2ultimod/superheroes` — toggle superheroes

**Note :** la table SQLite admins est gérée par Track D (`admin_admins`). La foundation ne fait que **consommer** via `IPermissionService` — Track D fournit l'implémentation.

---

## 5. Common utils (`Core/Utils/`)

### Chat / broadcast

```csharp
namespace CS2Ultimod.Core.Utils;

public static class Chat
{
    // Préfixe automatique [CS2Ultimod] coloré, FR par défaut.
    public static void Tell(CCSPlayerController player, string message);
    public static void TellError(CCSPlayerController player, string message);
    public static void TellSuccess(CCSPlayerController player, string message);

    public static void Broadcast(string message);
    public static void BroadcastError(string message);
    public static void BroadcastSuccess(string message);

    // HUD center
    public static void HudCenter(CCSPlayerController player, string message, float durationSec = 3f);
    public static void HudCenterAll(string message, float durationSec = 3f);
}
```

### Player lookups

```csharp
public static class PlayerExt
{
    public static IEnumerable<CCSPlayerController> AllAlive();
    public static IEnumerable<CCSPlayerController> AllConnected();
    public static IEnumerable<CCSPlayerController> InTeam(CsTeam team);

    public static CCSPlayerController? FindByName(string partial);    // case-insensitive partial match
    public static CCSPlayerController? FindBySteamId(ulong steamId64);

    // Multi-target (`@all`, `@t`, `@ct`, `@me`, `@!me`, `@spec`, `@dead`, `@alive`, partial name)
    public static IReadOnlyList<CCSPlayerController> Resolve(string token, CCSPlayerController? caller = null);
}
```

---

## 6. Command registry (`Core/Commands/`)

```csharp
public interface ICommandRegistry
{
    // Enregistre une commande chat (`!cmd`, `.cmd`, `/cmd`).
    // Lève si une commande de même nom existe déjà.
    void Register(ChatCommand cmd);
}

public sealed record ChatCommand(
    string Name,                                  // "kick" → !kick / .kick / /kick
    string[]? Aliases,                            // ["k"]
    string? RequiredFlag,                         // "@css/kick" ou null
    string Usage,                                 // "<player> [reason]"
    Action<CCSPlayerController, string[]> Handler,
    GameMode[]? AvailableInModes = null);         // null = tous les modes
```

**Règle :** chaque track maintient sa liste de commandes dans son module et les enregistre au boot. Les collisions sont des erreurs **bloquantes** (le plugin refuse de démarrer).

---

## 7. Mode lifecycle (côté tracks)

Chaque mode (Track A/B/C/H + Mixte) implémente :

```csharp
public interface IGameMode
{
    GameMode Mode { get; }

    // Activation : prepare l'état (configs, allocator hooks, etc.)
    Task OnEnterAsync(ModeContext ctx);

    // Désactivation : nettoie tout (handlers, entités, timers)
    Task OnExitAsync(ModeContext ctx);
}

public sealed record ModeContext(
    string MapName,
    GameMode? PreviousMode,
    string? Reason);
```

Le mode manager invoque `OnExitAsync` du mode courant **avant** `OnEnterAsync` du nouveau mode. Tracks **doivent** être idempotents et symétriques.

---

## 8. Conventions transverses

### Logging
```csharp
ILogger<T> via DI standard.
Préfixer messages : "[ModuleName]" pour grep facile.
```

### Localisation
- **Tous les messages utilisateur en français.**
- Pas de framework i18n en V1 (string literals OK).
- Constantes regroupées dans `Strings.cs` par module.

### Async
- Toute IO (DB, API Faceit, fichiers) en `async`.
- Jamais de `.Result` ou `.Wait()` sur le main thread CS2 (deadlock).
- Pour exécuter du code sur le main thread depuis async : `Server.NextFrame(() => ...)`.

### Configuration
- Configs JSON dans `configs/` (sample dans repo, override dans `configs/local.*.json` ignored).
- Chaque module charge la sienne : `core.json`, `retake.json`, `execute.json`, `pickup.json`, `admin.json`, etc.
- Hot-reload non requis V1.

### Erreurs
- Exceptions non gérées par track → loggées mais ne tuent pas le plugin.
- ModeManager wrap toutes les transitions dans try/catch pour éviter qu'un échec d'OnEnter laisse le serveur dans un état mort.

---

## 9. Ce que les agents doivent **savoir**

Quand un agent est briefé pour Track B (Execute), Track H (Pickup), ou Track D (Admin), il reçoit :
1. Ce fichier `INTERFACES.md`
2. Le `SCOPE.md`
3. Les sources upstream pertinentes (URLs GitHub)
4. La liste des commandes/tables qui lui sont **réservées**
5. La consigne : "ne touche pas aux modules hors de ton préfixe"

---

## 10. Versionnage des contrats

Ces interfaces sont en **v1**. Toute breaking change pendant Phase 1 (tracks en cours) doit être :
1. Annoncée explicitement par main session
2. Propagée à tous les agents actifs via SendMessage
3. Documentée dans un `CHANGELOG-INTERFACES.md` (à créer si nécessaire)

Préférer des extensions (nouveaux types/méthodes) plutôt que des modifications.
