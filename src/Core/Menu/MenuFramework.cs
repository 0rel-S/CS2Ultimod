using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Core.Menu;

// ── Public interfaces ───────────────────────────────────────────────────────

public interface IMenu
{
    string Title { get; set; }
    IMenu AddItem(string label, Action<CCSPlayerController> onSelect, bool enabled = true, string? color = null, bool keepOpen = false);
    // Variante "label dynamique" : la fonction est ré-évaluée à chaque tick, donc
    // les changements d'état (toggle ON/OFF, ✔ déplacé) apparaissent sans rouvrir le menu.
    IMenu AddItem(Func<string> labelFn, Action<CCSPlayerController> onSelect, bool enabled = true, Func<string?>? colorFn = null, bool keepOpen = false);
    IMenu AddSubmenu(string label, IMenu submenu);
    IMenu AddBack();
    // Rattache explicitement ce menu à un parent (retour = revenir à `parent`).
    // Utile quand le menu est ouvert hors d'un tick (callback async), donc hors de
    // portée de l'auto-rattachement géré par le framework.
    IMenu WithParent(IMenu parent);
    void Open(CCSPlayerController player);
    void Close(CCSPlayerController player);
}

public interface IMenuFramework
{
    IMenu Create(string title);
    IMenu CreateConfirm(string question, Action<CCSPlayerController> onYes, Action<CCSPlayerController>? onNo = null);
    IMenu CreatePlayerPicker(string title, Action<CCSPlayerController, CCSPlayerController> onPick, Func<CCSPlayerController, bool>? filter = null, bool keepOpen = false);
    void ShowBanner(string html, float durationSec);
}

// ── Implementation ──────────────────────────────────────────────────────────
// Pattern emprunté à schwarper/CS2MenuManager (WasdMenu) :
//  - freeze = VelocityModifier = 0f chaque tick (n'altère PAS les inputs)
//  - PrintToCenterHtml chaque tick → bat le fade ~1s natif de Source
//  - edge-detect au RELÂCHEMENT des boutons : (now & btn) == 0 && (old & btn) != 0

public sealed class MenuFramework : IMenuFramework
{
    private readonly Dictionary<int, MenuInstance> _open = [];
    private string? _activeBanner;
    private float _bannerExpiry;

    // Menu dont on traite actuellement la sélection (Use). Si son action ouvre un
    // autre menu (sous-écran construit à la volée : picker, confirm, sous-menu),
    // on rattache ce nouveau menu à celui-ci pour que Recharger y revienne.
    private MenuInstance? _handling;
    internal void BeginHandling(MenuInstance inst) => _handling = inst;
    internal void EndHandling() => _handling = null;

    public void ShowBanner(string html, float durationSec)
    {
        _activeBanner = html;
        _bannerExpiry = Server.CurrentTime + durationSec;
    }

    private int _tickDebugCounter;
    public void OnTick()
    {
        // Diag inconditionnel : prouve que CE listener (cette instance) tourne.
        // Si on voit "instance=X open=N" toutes les 4s, on sait que OnTick fire.
        // Si on voit "Opened" sur une instance Y ≠ X, c'est qu'il y a un listener fantôme.
        if ((++_tickDebugCounter % 256) == 0)
            CS2UltimodPlugin.Log?.LogInformation("[Menu] OnTick alive instance={Id:X8} open={N}",
                this.GetHashCode(), _open.Count);

        foreach (var (slot, inst) in _open.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || player.Connected != PlayerConnectedState.Connected)
            {
                CS2UltimodPlugin.Log?.LogWarning("[Menu] OnTick: removing slot {Slot} (player invalid)", slot);
                _open.Remove(slot);
                continue;
            }
            inst.Tick(player);
            // NB : pas de _open.Remove ici. CloseInstance/OpenInstance gèrent déjà
            // _open inline. Si Tick a fait Close(root)+Open(submenu), _open[slot]
            // pointe maintenant sur submenu — un Remove ici tuerait le submenu.
        }

        // Banner global (PrintToCenterAlert = bandeau bas-centre).
        if (_activeBanner == null) return;
        if (Server.CurrentTime >= _bannerExpiry) { _activeBanner = null; return; }
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.Connected != PlayerConnectedState.Connected) continue;
            // Évite d'écraser le HUD HTML d'un menu actif sur ce joueur.
            if (_open.ContainsKey(p.Slot)) continue;
            p.PrintToCenterAlert(_activeBanner);
        }
    }

    public IMenu Create(string title) => new MenuInstance(title, this);

    public IMenu CreateConfirm(string question, Action<CCSPlayerController> onYes, Action<CCSPlayerController>? onNo = null)
    {
        var m = Create(question);
        m.AddItem("Oui", onYes);
        m.AddItem("Non", onNo ?? (_ => { }));
        m.AddBack();
        return m;
    }

    public IMenu CreatePlayerPicker(string title, Action<CCSPlayerController, CCSPlayerController> onPick, Func<CCSPlayerController, bool>? filter = null, bool keepOpen = false)
    {
        var m = Create(title);
        foreach (var target in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && (filter == null || filter(p))))
            m.AddItem(target.PlayerName, caller => onPick(caller, target), keepOpen: keepOpen);
        m.AddBack();
        return m;
    }

    internal void OpenInstance(CCSPlayerController player, MenuInstance inst)
    {
        // Auto-rattachement : un menu ouvert pendant qu'on traite la sélection d'un
        // autre (action → picker/confirm/sous-menu) hérite de lui comme parent, sauf
        // s'il en a déjà un (AddSubmenu) ou si on lui en a fixé un (WithParent).
        if (_handling != null && _handling != inst)
            inst.AttachParent(_handling);

        if (_open.TryGetValue(player.Slot, out var prev) && prev != inst)
            prev.MarkClosed();
        _open[player.Slot] = inst;
        inst.OnOpened(player);
        // NB : pas d'appel PrintToCenterAlert("") ici. L'engine ignore les strings
        // vides → ça ne clear rien. Un PrintToCenterAlert(" ") rallume le panneau
        // VGUI pour 5s avec un caractère espace, ce qui crée une bande visible
        // par-dessus le menu HTML. Le fade naturel d'un Alert précédent (~5s) est
        // simplement non-tuable côté serveur — on l'accepte.
        CS2UltimodPlugin.Log?.LogInformation("[Menu] Opened '{Title}' slot={Slot} count={N} instance={Id:X8}",
            inst.Title, player.Slot, _open.Count, this.GetHashCode());
    }

    internal void CloseInstance(CCSPlayerController player, MenuInstance inst, string reason = "unknown")
    {
        CS2UltimodPlugin.Log?.LogInformation("[Menu] Close '{Title}' slot={Slot} reason={Reason}",
            inst.Title, player.Slot, reason);
        inst.MarkClosed();
        _open.Remove(player.Slot);
        // Restore VelocityModifier
        var pawn = player.PlayerPawn.Value;
        if (pawn != null && pawn.IsValid) pawn.VelocityModifier = 1.0f;
    }
}

// ── MenuInstance ────────────────────────────────────────────────────────────

internal sealed class MenuInstance : IMenu
{
    public string Title { get; set; }
    internal bool IsOpen { get; private set; }

    private readonly MenuFramework _framework;
    private readonly List<MenuItem> _items = [];
    private MenuInstance? _parent;
    private int _cursor;
    private int _offset;
    private PlayerButtons _oldButtons;
    private bool _firstTick;
    private bool _showBackFooter;

    // 5 items / page : aligne avec schwarper. Laisse de la place verticale pour
    // un titre qui wrap, le footer d'aide, et un éventuel HUD concurrent (bombe,
    // round info) qui s'affiche dans la même zone CenterHtml.
    private const int ItemsPerPage = 5;

    internal MenuInstance(string title, MenuFramework framework)
    {
        Title = title;
        _framework = framework;
    }

    public IMenu AddItem(string label, Action<CCSPlayerController> onSelect, bool enabled = true, string? color = null, bool keepOpen = false)
    {
        _items.Add(new MenuItem(() => label, onSelect, null, enabled, color != null ? () => color : null, keepOpen));
        return this;
    }

    public IMenu AddItem(Func<string> labelFn, Action<CCSPlayerController> onSelect, bool enabled = true, Func<string?>? colorFn = null, bool keepOpen = false)
    {
        _items.Add(new MenuItem(labelFn, onSelect, null, enabled, colorFn, keepOpen));
        return this;
    }

    public IMenu AddSubmenu(string label, IMenu submenu)
    {
        var sub = (MenuInstance)submenu;
        sub._parent = this;
        _items.Add(new MenuItem(() => label, null, sub, true));
        return this;
    }

    public IMenu AddBack()
    {
        // Sticky footer — handled by the Reload button + rendered as a non-selectable
        // last row on every page. Avoids the "Retour orphelin sur la dernière page"
        // problem of paginated menus.
        _showBackFooter = true;
        return this;
    }

    public IMenu WithParent(IMenu parent)
    {
        _parent = (MenuInstance)parent;
        return this;
    }

    // Ne pose le parent que s'il n'en a pas déjà un (n'écrase ni AddSubmenu ni WithParent).
    internal void AttachParent(MenuInstance parent) => _parent ??= parent;

    public void Open(CCSPlayerController player) => _framework.OpenInstance(player, this);
    public void Close(CCSPlayerController player) => _framework.CloseInstance(player, this, "external");

    internal void MarkClosed() => IsOpen = false;

    internal void OnOpened(CCSPlayerController player)
    {
        IsOpen = true;
        _cursor = 0;
        _offset = 0;
        _oldButtons = 0;
        _firstTick = true;
    }

    internal void Tick(CCSPlayerController player)
    {
        if (!IsOpen) return;

        // Render — chaque tick pour battre le fade natif.
        // Scroll continu : la fenêtre [_offset, _offset+ItemsPerPage) suit le curseur
        // ligne par ligne (cf. branchement plus bas), pas par sauts de page.
        var maxIndex = Math.Min(_offset + ItemsPerPage, _items.Count);

        var sb = new System.Text.StringBuilder();
        if (_items.Count > ItemsPerPage)
            sb.Append($"<font color='#FFD700'><b>{System.Net.WebUtility.HtmlEncode(Title)}</b></font> <font color='#888888' class='fontSize-s'>({_cursor + 1}/{_items.Count})</font><br>");
        else
            sb.Append($"<font color='#FFD700'><b>{System.Net.WebUtility.HtmlEncode(Title)}</b></font><br>");

        for (var i = _offset; i < maxIndex; i++)
        {
            var item = _items[i];
            var label = item.LabelFn();
            if (i == _cursor)
                sb.Append($"<font color='#FFD700'>▶ {System.Net.WebUtility.HtmlEncode(label)} ◀</font><br>");
            else if (!item.Enabled)
                sb.Append($"<font color='#666666'>  {System.Net.WebUtility.HtmlEncode(label)}</font><br>");
            else
                sb.Append($"<font color='{item.ColorFn?.Invoke() ?? "#FFFFFF"}'>  {System.Net.WebUtility.HtmlEncode(label)}</font><br>");
        }
        if (_showBackFooter && _parent != null)
            sb.Append("<font color='#AAAAAA'>  ↩ Retour (recharger)</font><br>");
        // Pas de lettre de touche : les keybinds CS2 sont client-side et locale-
        // dépendants (AZERTY ≠ QWERTY, et le joueur peut remapper). On affiche
        // les noms d'actions — chacun retrouve sa propre touche.
        sb.Append("<font color='#888888' class='fontSize-s'>Avancer/Reculer = naviguer · Utiliser = valider · Recharger = retour/fermer</font>");
        player.PrintToCenterHtml(sb.ToString());

        // Freeze (sans bloquer les inputs).
        var pawn = player.PlayerPawn.Value;
        if (pawn != null && pawn.IsValid) pawn.VelocityModifier = 0.0f;

        // Premier tick : on n'agit jamais sur l'input. _oldButtons=0 + cette garde
        // évitent qu'un bit transitoire (Use/Reload tenu lors de l'ouverture, ou
        // pulse pendant la fermeture du chat) ne déclenche un faux release-edge
        // qui fermerait le menu immédiatement.
        if (_firstTick)
        {
            _firstTick = false;
            _oldButtons = player.Buttons;
            return;
        }

        // Edge detect au RELÂCHEMENT (pattern schwarper) :
        // released = (now & btn) == 0  &&  (old & btn) != 0
        var now = player.Buttons;
        var released = ~now & _oldButtons;
        _oldButtons = now;

        if ((released & PlayerButtons.Forward) != 0 && _items.Count > 0)
        {
            _cursor = (_cursor - 1 + _items.Count) % _items.Count;
            // Scroll continu : ramener la fenêtre juste assez pour que le curseur
            // soit visible. Sur wrap (haut→bas) → fenêtre saute en fin.
            if (_cursor < _offset) _offset = _cursor;
            else if (_cursor >= _offset + ItemsPerPage) _offset = _cursor - ItemsPerPage + 1;
        }
        else if ((released & PlayerButtons.Back) != 0 && _items.Count > 0)
        {
            _cursor = (_cursor + 1) % _items.Count;
            if (_cursor < _offset) _offset = _cursor;
            else if (_cursor >= _offset + ItemsPerPage) _offset = _cursor - ItemsPerPage + 1;
        }
        else if ((released & PlayerButtons.Use) != 0)
        {
            var item = _items.ElementAtOrDefault(_cursor);
            if (item == null || !item.Enabled) return;
            if (item.Submenu != null)
            {
                _framework.CloseInstance(player, this, "use→submenu");
                _framework.OpenInstance(player, item.Submenu);
                // Render submenu's first frame now to overwrite the fading parent HUD.
                item.Submenu.Tick(player);
            }
            else if (item.KeepOpen)
            {
                // Le menu reste ouvert : on exécute juste l'action et on
                // retombe dans le tick courant. Reset des inputs pour ne
                // pas re-déclencher en chaîne (si le joueur tient Use).
                // BeginHandling : si l'action ouvre un sous-écran, il sera rattaché ici.
                _framework.BeginHandling(this);
                try { item.OnSelect?.Invoke(player); }
                finally { _framework.EndHandling(); }
                _oldButtons = player.Buttons;
            }
            else
            {
                _framework.CloseInstance(player, this, "use→select");
                // BeginHandling : si l'action ouvre un sous-écran (picker, confirm,
                // sous-menu construit à la volée), OpenInstance le rattache à `this`.
                _framework.BeginHandling(this);
                try { item.OnSelect?.Invoke(player); }
                finally { _framework.EndHandling(); }
            }
        }
        else if ((released & PlayerButtons.Reload) != 0)
        {
            // R fait double emploi : retour parent en sous-menu, fermeture en racine.
            if (_parent != null)
            {
                _framework.CloseInstance(player, this, "reload→parent");
                _framework.OpenInstance(player, _parent);
                _parent.Tick(player);
            }
            else
            {
                _framework.CloseInstance(player, this, "reload→exit");
            }
        }
    }
}

internal sealed record MenuItem(
    Func<string> LabelFn,
    Action<CCSPlayerController>? OnSelect,
    MenuInstance? Submenu,
    bool Enabled,
    Func<string?>? ColorFn = null,
    bool KeepOpen = false);
